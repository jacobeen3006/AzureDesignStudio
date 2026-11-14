using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using AzureDesignStudio.Services;
using AzureDesignStudio.Services.Models;
using AzureDesignStudio.Discovery.Iac.WhatIf;
using AzureDesignStudio.Server.Persistence;
using AzureDesignStudio.Server.Services;
using AzureDesignStudio.Server.Utils;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Identity.Web;

var builder = WebApplication.CreateBuilder(args);

// Desktop talks native gRPC (HTTP/2) directly; cleartext HTTP/2 (h2c) needs its own
// loopback listener because ALPN negotiation requires TLS. Browsers keep the
// URL-configured endpoints and gRPC-Web over HTTP/1.1 - which only holds because
// DesktopGrpcEndpoint re-binds them: an endpoint defined in code otherwise replaces
// every URL-configured one. Development only unless DesktopGrpc:Port is set.
var desktopGrpcPort = DesktopGrpcEndpoint.ResolvePort(builder.Configuration, builder.Environment);
if (desktopGrpcPort > 0)
{
    builder.WebHost.ConfigureKestrel((context, options) =>
        DesktopGrpcEndpoint.Add(options, context.Configuration, desktopGrpcPort));
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("AzureAd"));
builder.Services.Configure<JwtBearerOptions>(
    JwtBearerDefaults.AuthenticationScheme, options =>
    {
        options.TokenValidationParameters.NameClaimType = "name";
    });

// The one value read before Build(): the vault decision below needs it. It is resolved once and
// carried to the DbContext registration, so the vault and the EF provider cannot diverge. Tests
// must therefore supply Database:Provider with UseSetting, not ConfigureAppConfiguration (a late
// source would be ignored, not silently disagree).
var databaseKind = DatabaseProvider.Resolve(builder.Configuration);

// Key Vault supplies configuration (the "ads-main" connection string among it) when one is
// named. The checked-in appsettings.json names a placeholder, "<kv name>": building the vault
// URL from it threw UriFormatException before anything could say what was missing.
// Only the SQL Server provider needs a vault-supplied connection string; a Sqlite or InMemory
// host must never make a vault round trip at boot.
var keyVaultName = builder.Configuration["KeyVaultName"];
if (databaseKind == DatabaseKind.SqlServer && ServiceTools.IsConfigured(keyVaultName))
{
    var secretClient = new SecretClient(
        new Uri($"https://{keyVaultName}.vault.azure.net/"),
        new DefaultAzureCredential());
    builder.Configuration.AddAzureKeyVault(secretClient, new KeyVaultSecretManager());
}

// The callback runs when the first context is built (inside the startup bootstrap, before any
// request), so a connection string from the vault source or from late configuration is
// honoured and a missing one still stops the host before it serves.
builder.Services.AddDbContext<DesignDbContext>((serviceProvider, options) =>
    DatabaseProvider.Configure(options, databaseKind, serviceProvider.GetRequiredService<IConfiguration>()));

// The provider is read from configuration (Crypto:Provider, default KeyVault); the real service
// contacts the vault lazily on first use, so the host boots without one. This runs at top
// level, so tests that need another value must supply it with UseSetting.
CryptoProvider.Register(builder.Services, builder.Configuration);

builder.Services.AddSingleton<ITelemetryInitializer, AdsTelemetryInitializer>();
builder.Services.AddApplicationInsightsTelemetry();

builder.Services.AddRazorPages();
builder.Services.AddGrpc(options => options.Interceptors.Add<CallerInterceptor>());
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("RetailPrices", client => client.Timeout = TimeSpan.FromSeconds(30));

builder.Services.AddSingleton(new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl));
builder.Services.AddHttpClient("arm", client => client.Timeout = TimeSpan.FromSeconds(120));
if (builder.Environment.IsDevelopment())
    builder.Services.AddGrpcReflection();
// For running behind an ingress
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

// The schema is brought up once, here. The gRPC services used to call EnsureCreated in
// their constructors - a synchronous database round trip on every request.
DatabaseBootstrapper.Initialize(app.Services);

// Configure the HTTP request pipeline.
// Forwarded headers first: behind a TLS-terminating ingress the request only reads as
// HTTPS once X-Forwarded-Proto is applied, and HSTS is emitted for HTTPS requests alone.
app.UseForwardedHeaders();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// The Desktop listener is cleartext by design and gRPC clients do not follow redirects,
// so it is the one endpoint that must not be sent to HTTPS.
app.UseWhen(
    context => desktopGrpcPort == 0 || context.Connection.LocalPort != desktopGrpcPort,
    branch => branch.UseHttpsRedirection());

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.UseGrpcWeb();
app.MapGrpcService<HostedDesignService>().EnableGrpcWeb();
app.MapGrpcService<HostedDeployService>().EnableGrpcWeb();
app.MapGrpcService<RetailPriceService>().EnableGrpcWeb();

if (app.Environment.IsDevelopment())
{
    app.MapGrpcReflectionService();
}

app.MapStaticAssets();
app.MapRazorPages();
app.MapFallbackToFile("index.html");

app.Run();

// Exposes the entry point as the type argument of WebApplicationFactory<Program> in the server tests.
public partial class Program { }
