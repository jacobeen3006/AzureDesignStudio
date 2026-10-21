using System.Net;
using System.Net.Http.Headers;
using BlazorApplicationInsights;
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
        var serverUrl = builder.Configuration["ServerUrl"] ?? "http://localhost:5204";
        var resourceRoot = builder.Configuration["ResourceRoot"] ?? "/";

        var webViewOrigin = new Uri("https://0.0.0.1/");
        builder.Services.AddHttpClient("AzureDesignStudio.Root", (sp, client) => client.BaseAddress = webViewOrigin)
            .ConfigurePrimaryHttpMessageHandler(() => new PackageAssetHandler(webViewOrigin));
        builder.Services.AddHttpClient("AzureDesignStudio.ResourceAccess", (sp, client) =>
            client.BaseAddress = Uri.TryCreate(resourceRoot, UriKind.Absolute, out var abs) ? abs : webViewOrigin)
            .ConfigurePrimaryHttpMessageHandler(() => new PackageAssetHandler(webViewOrigin));

        // Native gRPC (HTTP/2) directly against the server; the browser-only
        // Grpc.Net.Client.Web shim from the WASM host is not needed here.
        // Client names match the shared service wrappers' GrpcClientFactory lookups;
        // the [Authorize] endpoints get their bearer token from the MSAL handler.
        // SQLite workspace (plan 6.3): designs, scans, snapshots, cost cache, plans and audit
        // records in one file - everything readable with the network down. Designs saved while
        // the server is unreachable stay pending until a successful sync clears them; the v1
        // per-file pending cache is imported once on startup.
        var workspaceDb = new WorkspaceDb(System.IO.Path.Combine(FileSystem.Current.AppDataDirectory, "workspace.db"));
        LegacyDesignImporter.ImportIfNeeded(workspaceDb, System.IO.Path.Combine(FileSystem.Current.AppDataDirectory, "designs"));
        builder.Services.AddSingleton(workspaceDb);
        builder.Services.AddSingleton<AzureDesignStudio.Core.Common.IDesignOfflineStore, SqliteDesignOfflineStore>();
        builder.Services.AddScoped<DesignGrpcService>();
        builder.Services.AddGrpcClient<Design.DesignClient>("DesignClientWithAuth", o => o.Address = new Uri(serverUrl))
            .AddHttpMessageHandler<MsalAuthTokenHandler>();
        builder.Services.AddScoped<DeployGrpcService>();
        builder.Services.AddGrpcClient<Deploy.DeployClient>("DeployClientWithAuth", o => o.Address = new Uri(serverUrl))
            .AddHttpMessageHandler<MsalAuthTokenHandler>();
        builder.Services.AddScoped<CostGrpcService>();
        builder.Services.AddScoped<CostService>();
        builder.Services.AddGrpcClient<Cost.CostClient>("CostClient", o => o.Address = new Uri(serverUrl));

        // Native MSAL (plan 4.2): work + personal accounts via the Windows broker
        // (WAM) with device-code fallback; state mirrors the serialized cache.
        builder.Services.AddSingleton<MsalAccountService>();

        // Single token seam (plan 6.2): the broker serves the gRPC bearer handler and is the
        // Azure.Identity TokenCredential for every direct Azure SDK client.
        builder.Services.AddSingleton<AdsTokenBroker>();
        builder.Services.AddSingleton<Azure.Core.TokenCredential>(sp => sp.GetRequiredService<AdsTokenBroker>());
        builder.Services.AddTransient<MsalAuthTokenHandler>();

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
        builder.Services.AddBlazorApplicationInsights();
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
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            System.IO.File.AppendAllText(path, $"[{DateTime.Now:O}] {logLevel} {eventId}\n{formatter(state, exception)}\n{exception}\n\n");
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
