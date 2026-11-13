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

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ads_desktop_crash.log"),
                $"[{DateTime.Now:O}] UNHANDLED\n{e.ExceptionObject}\n\n");
        TaskScheduler.UnobservedTaskException += (s, e) =>
            System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ads_desktop_crash.log"),
                $"[{DateTime.Now:O}] UNOBSERVED\n{e.Exception}\n\n");
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            });

        builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);
        var resourceRoot = builder.Configuration["ResourceRoot"] ?? "/";

        var webViewOrigin = new Uri("https://0.0.0.1/");
        builder.Services.AddHttpClient("AzureDesignStudio.Root", (sp, client) => client.BaseAddress = webViewOrigin)
            .ConfigurePrimaryHttpMessageHandler(() => new PackageAssetHandler(webViewOrigin));
        builder.Services.AddHttpClient("AzureDesignStudio.ResourceAccess", (sp, client) =>
            client.BaseAddress = Uri.TryCreate(resourceRoot, UriKind.Absolute, out var abs) ? abs : webViewOrigin)
            .ConfigurePrimaryHttpMessageHandler(() => new PackageAssetHandler(webViewOrigin));

        // SQLite workspace (plan 6.3): designs, scans, snapshots, cost cache, plans and audit
        // records in one file - everything readable with the network down. The v1 per-file
        // design cache is imported once on startup.
        var appData = FileSystem.Current.AppDataDirectory;
        var workspaceDb = new WorkspaceDb(System.IO.Path.Combine(appData, "workspace.db"));
        LegacyDesignImporter.ImportIfNeeded(workspaceDb, System.IO.Path.Combine(appData, "designs"));
        builder.Services.AddSingleton(workspaceDb);
        builder.Services.AddSingleton<AzureDesignStudio.Core.Common.IDesignOfflineStore, SqliteDesignOfflineStore>();

        // Standalone: no server. The design, deploy and price services run in this process
        // (AzureDesignStudio.Services, the same classes the hosted server exposes), reached by
        // the shared client code through generated gRPC clients on an in-process invoker.
        // Deploy approvals, guardrails, linked subscriptions and audit events go to deploy.db.
        builder.Services.AddDbContext<AzureDesignStudio.Services.Models.DesignDbContext>(options =>
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
        // (WAM) with device-code fallback; state mirrors the serialized cache.
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
        builder.Services.AddSingleton(new AdsHostFeatures
        {
            TenantScanLabel = "Tenant scan",
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

        // deploy.db: created with the full schema on first run. (A later schema change needs a
        // migration - EnsureCreated never alters an existing file.)
        using (var scope = app.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<AzureDesignStudio.Services.Models.DesignDbContext>().Database.EnsureCreated();
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

// The WebView2 virtual host (https://0.0.0.1/) is only resolvable inside the WebView's
// JS network stack; .NET HttpClient would hit an unreachable IP. Serve same-origin
// requests straight from the packaged wwwroot, everything else goes to the real network.
internal sealed class PackageAssetHandler(Uri webViewOrigin) : HttpMessageHandler
{
    private readonly HttpClient _network = new(new HttpClientHandler());

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        if (uri.Host == webViewOrigin.Host && (uri.Scheme == "https" || uri.Scheme == "http"))
        {
            var path = uri.AbsolutePath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var file = Path.Combine(AppContext.BaseDirectory, "wwwroot", path);
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
