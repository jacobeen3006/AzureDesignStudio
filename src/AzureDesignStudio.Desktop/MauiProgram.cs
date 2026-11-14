using System.Net;
using System.Net.Http.Headers;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Services;
using AzureDesignStudio.Workspace;
using AzureDesignStudio.Discovery.Arm;
using AzureDesignStudio.Discovery.Preflight;
using AzureDesignStudio.Discovery.ResourceGraph;
using AzureDesignStudio.Discovery.Scan;
using AzureDesignStudio.SharedModels.Protos;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;

namespace AzureDesignStudio.Desktop;

public static class MauiProgram
{
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>Set when the workspace (or deploy database) could not be upgraded: the app then
    /// shows <see cref="StartupFailurePage"/> instead of the main window and no other service runs.</summary>
    public static Exception? StartupFailure { get; private set; }

    /// <summary>The folder that holds workspace.db, deploy.db and the backups (the platform app-data
    /// folder, or the <c>ADS_DATA_DIR</c> override).</summary>
    public static string DataDirectory { get; private set; } = string.Empty;

    private static readonly string CrashLogPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ads_desktop_crash.log");

    private static void LogCrash(string label, Exception e)
    {
        try
        {
            System.IO.File.AppendAllText(CrashLogPath, $"[{DateTime.Now:O}] {label}\n{e}\n\n");
        }
        catch (Exception write) when (write is System.IO.IOException or UnauthorizedAccessException)
        {
        }
    }

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ads_desktop_crash.log"),
                $"[{DateTime.Now:O}] UNHANDLED\n{e.ExceptionObject}\n\n");
        TaskScheduler.UnobservedTaskException += (s, e) =>
            System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ads_desktop_crash.log"),
                $"[{DateTime.Now:O}] UNOBSERVED\n{e.Exception}\n\n");
#if MACCATALYST
        MacHost.ExtendToolPath();
#endif
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

#if MACCATALYST
        // Bundled into Contents/Resources, not next to the executable (Contents/MonoBundle).
        builder.Configuration.AddJsonFile(AppContent.SettingsFile, optional: true, reloadOnChange: false);
#else
        builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
#endif
        var resourceRoot = builder.Configuration["ResourceRoot"] ?? "/";

        var webViewOrigin = new Uri("https://0.0.0.1/");
        builder.Services.AddHttpClient("AzureDesignStudio.Root", (sp, client) => client.BaseAddress = webViewOrigin)
            .ConfigurePrimaryHttpMessageHandler(() => new PackageAssetHandler(webViewOrigin));
        // Only an http(s) ResourceRoot is a remote root. On macOS "/" also parses as an absolute
        // URI (file:///), which sent every icon request to the file scheme instead of the package.
        builder.Services.AddHttpClient("AzureDesignStudio.ResourceAccess", (sp, client) =>
            client.BaseAddress = Uri.TryCreate(resourceRoot, UriKind.Absolute, out var abs) && (abs.Scheme == Uri.UriSchemeHttps || abs.Scheme == Uri.UriSchemeHttp)
                ? abs : webViewOrigin)
            .ConfigurePrimaryHttpMessageHandler(() => new PackageAssetHandler(webViewOrigin));

        // SQLite workspace (plan 6.3): designs, scans, snapshots, cost cache, plans and audit
        // records in one file - everything readable with the network down. The v1 per-file
        // design cache is imported once on startup.
        // ADS_DATA_DIR redirects every database and backup to another folder. It exists so the built
        // app can be tested against copies of a workspace (or a corrupt file) without touching the
        // real data; normal use needs nothing and the defaults below are unchanged.
        var dataOverride = Environment.GetEnvironmentVariable("ADS_DATA_DIR");
        string appData;
        if (!string.IsNullOrWhiteSpace(dataOverride))
        {
            appData = System.IO.Path.GetFullPath(dataOverride);
            System.IO.Directory.CreateDirectory(appData);
        }
        else
        {
            appData =
#if MACCATALYST
                MacHost.AppDataDirectory;
#else
                FileSystem.Current.AppDataDirectory;
#endif
        }
        DataDirectory = appData;

        var workspacePath = System.IO.Path.Combine(appData, "workspace.db");
        var backupsDirectory = System.IO.Path.Combine(appData, "backups");
        WorkspaceStartupResult startup;
        try
        {
            startup = WorkspaceStartup.Open(new WorkspaceStartupOptions(workspacePath, backupsDirectory));
        }
        catch (WorkspaceUpgradeException e)
        {
            // An interrupted or failed upgrade must not open a half-working workspace: a native page
            // names the error and the premigration backup. Nothing else is needed to show it.
            StartupFailure = e;
            LogCrash("WORKSPACE STARTUP FAILED", e);
            var failed = builder.Build();
            Services = failed.Services;
            return failed;
        }

        var workspaceDb = startup.Db;
        LegacyDesignImporter.ImportIfNeeded(workspaceDb, System.IO.Path.Combine(appData, "designs"));
        builder.Services.AddSingleton(workspaceDb);
        builder.Services.AddSingleton(startup.Report);
        var backupService = new WorkspaceBackupService(workspacePath, backupsDirectory,
            onError: e =>
            {
                // The container exists by the time the scheduler ticks; the file is the fallback.
                if (Services?.GetService<ILoggerFactory>() is { } loggers)
                    loggers.CreateLogger("WorkspaceBackup").LogWarning(e, "A workspace backup step failed.");
                else
                    LogCrash("WORKSPACE BACKUP", e);
            });
        builder.Services.AddSingleton(backupService);
        builder.Services.AddSingleton<AzureDesignStudio.Core.Common.IDesignOfflineStore, SqliteDesignOfflineStore>();

        // Standalone: no server. The design, deploy and price services run in this process
        // (AzureDesignStudio.Services, the same classes the hosted server exposes), reached by
        // the shared client code through generated gRPC clients on an in-process invoker.
        // Deploy approvals, guardrails, linked subscriptions and audit events go to deploy.db.
        // The services ask for DesignDbContext; the SQLite flavour owns deploy.db's migrations.
        builder.Services.AddDbContext<AzureDesignStudio.Services.Models.DesignDbContext, AzureDesignStudio.Services.Models.SqliteDesignDbContext>(options =>
            options.UseSqlite($"Data Source={System.IO.Path.Combine(appData, "deploy.db")}"));
        builder.Services.AddSingleton(new AzureDesignStudio.Discovery.Iac.WhatIf.WhatIfTokenGate(
            AzureDesignStudio.Discovery.Iac.WhatIf.WhatIfTokenGate.DefaultTtl));
        builder.Services.AddMemoryCache();
        builder.Services.AddHttpClient("arm", client => client.Timeout = TimeSpan.FromSeconds(120));
        builder.Services.AddHttpClient("RetailPrices", client => client.Timeout = TimeSpan.FromSeconds(30));
        builder.Services.AddSingleton<AzureDesignStudio.Services.ICryptoService, CredentialStoreSecretProtector>();
        builder.Services.AddSingleton<AzureDesignStudio.Services.IAmbientAzureCredential, DesktopAmbientCredential>();
        builder.Services.AddScoped<Design.DesignBase, AzureDesignStudio.Services.LocalDesignService>();
        builder.Services.AddScoped<Deploy.DeployBase, AzureDesignStudio.Services.DeployService>();
        builder.Services.AddScoped<Cost.CostBase, AzureDesignStudio.Services.RetailPriceService>();
        builder.Services.AddSingleton(sp =>
        {
            var identity = sp.GetRequiredService<DesktopIdentity>();
            return new AzureDesignStudio.Services.InProcess.InProcessCallInvoker(sp, ct => identity.GetAsync(ct))
                .Add<Design.DesignBase>(Design.Descriptor.FullName, Design.BindService)
                .Add<Deploy.DeployBase>(Deploy.Descriptor.FullName, Deploy.BindService)
                .Add<Cost.CostBase>(Cost.Descriptor.FullName, Cost.BindService);
        });
        builder.Services.AddSingleton<Grpc.Net.ClientFactory.GrpcClientFactory, InProcessGrpcClientFactory>();
        // No offline cache in front of the design service: the service *is* the local store,
        // and the cache's "synced" step would delete the design it had just saved.
        builder.Services.AddScoped(sp => new DesignGrpcService(sp.GetRequiredService<Grpc.Net.ClientFactory.GrpcClientFactory>(), []));
        builder.Services.AddScoped<DeployGrpcService>();
        builder.Services.AddScoped<CostGrpcService>();
        builder.Services.AddScoped<CostService>();

        // Native MSAL (plan 4.2): work + personal accounts via the Windows broker
        // (WAM) or, on macOS, the system browser, with device-code fallback; state
        // mirrors the serialized cache.
        builder.Services.AddSingleton<MsalAccountService>();
        builder.Services.AddSingleton<DesktopIdentity>();

        // Single token seam (plan 6.2): the TokenCredential for every Azure SDK client.
        builder.Services.AddSingleton<AdsTokenBroker>();
        builder.Services.AddSingleton<Azure.Core.TokenCredential>(sp => sp.GetRequiredService<AdsTokenBroker>());

        // Tenant discovery (plan 7): ARG spine + ARM transport ride the broker credential;
        // scan pages checkpoint into the SQLite workspace so a kill resumes, not restarts.
        builder.Services.AddSingleton(sp => new ArmTransport(
            new HttpClient { Timeout = TimeSpan.FromMinutes(5) },
            sp.GetRequiredService<Azure.Core.TokenCredential>(), ArmEndpointSet.AzureCloud));
        builder.Services.AddSingleton(sp => new ResourceGraphClient(
            new HttpClient { Timeout = TimeSpan.FromMinutes(5) },
            sp.GetRequiredService<Azure.Core.TokenCredential>()));
        builder.Services.AddSingleton<TenantInventory>();
        builder.Services.AddSingleton<PermissionPreflight>();
        builder.Services.AddSingleton<WorkspaceScans>();
        builder.Services.AddSingleton<WorkspaceChanges>();
        builder.Services.AddSingleton<WorkspaceDecomWindows>();
        builder.Services.AddSingleton<ChangeFeedService>();
        builder.Services.AddSingleton<AzureDesignStudio.Discovery.Cost.ActualSpendClient>();
        builder.Services.AddSingleton<AzureDesignStudio.Discovery.Iac.DeploymentStackClient>();
        builder.Services.AddSingleton<AzureDesignStudio.Discovery.Naming.NameAvailabilityClient>();
        builder.Services.AddSingleton<AzureDesignStudio.Discovery.Security.ResourceLockClient>();
        builder.Services.AddSingleton<WorkspaceScanSink>();
        builder.Services.AddSingleton<AzureDesignStudio.Discovery.Scan.IScanSink>(sp => sp.GetRequiredService<WorkspaceScanSink>());
        builder.Services.AddSingleton<ScanEngine>();
        builder.Services.AddScoped<AuthenticationStateProvider, DesktopAuthenticationStateProvider>();
        builder.Services.AddAuthorizationCore();

        // The shared drawers call these instead of the WASM server-redirect flow.
        builder.Services.AddSingleton(sp => new AdsHostAuth
        {
            SignIn = () => sp.GetRequiredService<MsalAccountService>().SignInAsync(),
            SignOut = async () => await sp.GetRequiredService<MsalAccountService>().SignOutAsync(),
        });


        builder.Services.AddMauiBlazorWebView();
        builder.Services.AddAntDesign();
        builder.Services.AddSingleton<AdsContext>();
        builder.Services.AddSingleton<AzureDesignStudio.Services.AdsCommandService>();
        builder.Services.AddAutoMapper(typeof(AzureNodeProfile));
        builder.Services.AddAdsBicepDecompiler();
        builder.Services.AddSingleton<TenantScanSession>();
        // Tenant scan drawer (plan 7.8): the shared TopMenu opens it through the seam.
        // DrawerService must be the current circuit's instance - the func gets the
        // scoped provider straight from the click site in TopMenu.
        AntDesign.DrawerRef<string>? scanDrawer = null;
        AntDesign.DrawerRef<string>? workspaceDrawer = null;
        builder.Services.AddSingleton(new AdsHostFeatures
        {
            TenantScanLabel = "Tenant scan",
            WorkspaceDataLabel = "Workspace data",
            DesignsAreLocal = true,
            OpenTenantScan = async circuitServices =>
            {
                var drawers = circuitServices.GetRequiredService<AntDesign.DrawerService>();
                // TopMenu only tracks drawers it opened itself; close a previous
                // scan drawer here so repeated menu clicks never stack.
                if (scanDrawer is not null)
                    await scanDrawer.CloseAsync();
                var drawerRef = await drawers.CreateAsync<Components.TenantScanDrawerTemplate, string, string>(
                    new AntDesign.DrawerOptions { Title = "Tenant scan", Width = "720" }, string.Empty);
                drawerRef.OnClose = () => { scanDrawer = null; return Task.CompletedTask; };
                scanDrawer = drawerRef;
            },
            OpenWorkspaceData = async circuitServices =>
            {
                var drawers = circuitServices.GetRequiredService<AntDesign.DrawerService>();
                if (workspaceDrawer is not null)
                    await workspaceDrawer.CloseAsync();
                var drawerRef = await drawers.CreateAsync<Components.WorkspaceDataDrawerTemplate, string, string>(
                    new AntDesign.DrawerOptions { Title = "Workspace data", Width = "720" }, string.Empty);
                drawerRef.OnClose = () => { workspaceDrawer = null; return Task.CompletedTask; };
                workspaceDrawer = drawerRef;
            },
            // v2 10.4 online half: the naming drawer's live availability rides the same ARM
            // plumbing; the browser host leaves these null and the affordance vanishes.
            ListNameCheckSubscriptions = async circuitServices =>
            {
                var subs = await circuitServices.GetRequiredService<TenantScanSession>().GetSubscriptionsAsync();
                return subs.Select(s => (s.SubscriptionId, s.DisplayName)).ToList();
            },
            CheckNameAvailability = async (circuitServices, stencilKey, name, subscriptionId, location) =>
            {
                var result = await circuitServices.GetRequiredService<AzureDesignStudio.Discovery.Naming.NameAvailabilityClient>()
                    .CheckAsync(stencilKey, name, subscriptionId, location);
                return result.Outcome switch
                {
                    AzureDesignStudio.Discovery.Naming.NameCheckOutcome.Available => new AdsHostFeatures.AdsNameCheckResult(true, null),
                    AzureDesignStudio.Discovery.Naming.NameCheckOutcome.Unavailable => new AdsHostFeatures.AdsNameCheckResult(false, result.Detail),
                    // NotCheckable / Error: the drawer shows why in gray - never a fake verdict.
                    _ => new AdsHostFeatures.AdsNameCheckResult(null, result.Detail),
                };
            },
        });

        builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
        builder.Logging.AddProvider(new FileLoggerProvider(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ads_desktop_crash.log")));

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();
        Services = app.Services;

        // deploy.db is under EF migrations: a new file gets the baseline, a file an earlier build
        // created from the model adopts it, and an existing file is backed up (privately, in
        // backups/deploy) before any pending migration. A later schema change is a migration.
        using (var scope = app.Services.CreateScope())
            DeployDbStartup.Migrate(
                scope.ServiceProvider.GetRequiredService<AzureDesignStudio.Services.Models.DesignDbContext>(),
                System.IO.Path.Combine(appData, "deploy.db"),
                System.IO.Path.Combine(appData, "backups", "deploy"));
        return app;
    }

}

internal sealed class FileLoggerProvider(string path) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new FileLogger(path);
    public void Dispose() { }

    private sealed class FileLogger(string path) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        // One writer at a time, and a failed write is dropped: two threads appending at once
        // collide with an IOException, which the logging pipeline rethrows at the call site -
        // including inside catch blocks whose only job was to log.
        private static readonly object WriteLock = new();

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var entry = $"[{DateTime.Now:O}] {logLevel} {eventId}\n{formatter(state, exception)}\n{exception}\n\n";
            lock (WriteLock)
            {
                try
                {
                    System.IO.File.AppendAllText(path, entry);
                }
                catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
                {
                }
            }
        }
    }
}

// The WebView virtual host (0.0.0.1) is only resolvable inside the WebView's
// JS network stack; .NET HttpClient would hit an unreachable IP. Serve same-origin
// requests straight from the packaged wwwroot, everything else goes to the real network.
internal sealed class PackageAssetHandler(Uri webViewOrigin) : HttpMessageHandler
{
    // An invoker, not an HttpClient: the request already went through the calling HttpClient,
    // and a second HttpClient refuses a request message that was sent once.
    private readonly HttpMessageInvoker _network = new(new HttpClientHandler());

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.Host == webViewOrigin.Host && (uri.Scheme == "https" || uri.Scheme == "http"))
        {
            var path = uri.AbsolutePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var file = Path.Combine(AppContent.Root, "wwwroot", path);
            if (File.Exists(file))
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(await File.ReadAllBytesAsync(file, cancellationToken)),
                };
                response.Content.Headers.ContentType = new MediaTypeHeaderValue(Path.GetExtension(file) switch
                {
                    ".json" => "application/json",
                    ".svg" => "image/svg+xml",
                    ".css" => "text/css",
                    ".js" => "text/javascript",
                    ".png" => "image/png",
                    ".jpg" => "image/jpeg",
                    _ => "application/octet-stream",
                });
                return response;
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
        }
        return await _network.SendAsync(request, cancellationToken);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _network.Dispose();
        }
        base.Dispose(disposing);
    }
}
