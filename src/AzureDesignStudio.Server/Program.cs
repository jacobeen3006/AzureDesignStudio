using Azure.Extensions.AspNetCore.Configuration.Secrets;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using AzureDesignStudio.Server.Models;
using AzureDesignStudio.Discovery.Iac.WhatIf;
using AzureDesignStudio.Server.Services;
using AzureDesignStudio.Server.Utils;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
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

#if DEBUG
builder.Services.AddDbContext<DesignDbContext>(options => options.UseInMemoryDatabase("adsdb-inmem"));
#else
// Key Vault supplies configuration (the "ads-main" connection string among it) when one is
// named. The checked-in appsettings.json names a placeholder, "<kv name>": building the vault
// URL from it threw UriFormatException before anything could say what was missing.
var keyVaultName = builder.Configuration["KeyVaultName"];
if (ServiceTools.IsConfigured(keyVaultName))
{
    var secretClient = new SecretClient(
        new Uri($"https://{keyVaultName}.vault.azure.net/"),
        new DefaultAzureCredential());
    builder.Configuration.AddAzureKeyVault(secretClient, new KeyVaultSecretManager());
}

// The database: SQL Server, from the vault's "ads-main" secret or from ordinary
// configuration (ConnectionStrings__ads-main, e.g. a Kubernetes secret). Database:Provider
// set to InMemory runs without one - for a smoke test of the image, never for real use:
// nothing survives a restart.
var connectionString = builder.Configuration["ads-main"] ?? builder.Configuration.GetConnectionString("ads-main");
if (string.Equals(builder.Configuration["Database:Provider"], "InMemory", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddDbContext<DesignDbContext>(options => options.UseInMemoryDatabase("adsdb-inmem"));
}
else if (ServiceTools.IsConfigured(connectionString))
{
    builder.Services.AddDbContext<DesignDbContext>(options => options.UseSqlServer(connectionString));
}
else
{
    throw new InvalidOperationException(
        "No database is configured. Name a Key Vault that holds an \"ads-main\" connection string (KeyVaultName), " +
        "or set ConnectionStrings__ads-main. Database__Provider=InMemory starts the server without one, for testing only.");
}
#endif

builder.Services.AddSingleton<ICryptoService, CryptoService>();

builder.Services.AddSingleton<ITelemetryInitializer, AdsTelemetryInitializer>();
builder.Services.AddApplicationInsightsTelemetry();

builder.Services.AddRazorPages();
builder.Services.AddGrpc();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("RetailPrices", client => client.Timeout = TimeSpan.FromSeconds(30));

builder.Services.AddSingleton(new WhatIfTokenGate(WhatIfTokenGate.DefaultTtl));
builder.Services.AddHttpClient("arm", client => client.Timeout = TimeSpan.FromSeconds(120));
#if DEBUG
builder.Services.AddGrpcReflection();
#endif
// For running behind an ingress
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();

#if !DEBUG
if (string.Equals(app.Configuration["Database:Provider"], "InMemory", StringComparison.OrdinalIgnoreCase))
    app.Logger.LogWarning("Database:Provider is InMemory: designs, links and approvals are lost when the server stops. Use it for testing only.");
#endif

// The schema is brought up once, here. The gRPC services used to call EnsureCreated in
// their constructors - a synchronous database round trip on every request.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DesignDbContext>();
    db.Database.EnsureCreated();
#if !DEBUG
    // EnsureCreated creates a brand-new database with the full schema, and does nothing at
    // all for one that already exists - it never adds a missing table. The guarded DDL below
    // is what brings a database created by an earlier release up to date.
    {
        if (db.Database.IsRelational())
        {
            db.Database.ExecuteSqlRaw("""
                IF OBJECT_ID(N'AdsPlanApprovals', N'U') IS NULL
                CREATE TABLE AdsPlanApprovals (
                    PlanHash nvarchar(71) NOT NULL PRIMARY KEY,
                    PlanJson nvarchar(max) NOT NULL,
                    Status nvarchar(20) NOT NULL,
                    ApprovedBy nvarchar(320) NOT NULL,
                    ApprovedByUserId nvarchar(64) NOT NULL,
                    ApprovedAtUtc datetime2 NOT NULL DEFAULT GETUTCDATE(),
                    RevertPlanJson nvarchar(max) NULL);
                IF OBJECT_ID(N'AdsPlanApplyEvents', N'U') IS NULL
                CREATE TABLE AdsPlanApplyEvents (
                    Id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
                    ApplyId uniqueidentifier NOT NULL,
                    PlanHash nvarchar(71) NOT NULL,
                    PlanJson nvarchar(max) NOT NULL,
                    SubscriptionId nvarchar(64) NOT NULL,
                    ResourceGroupName nvarchar(90) NOT NULL,
                    TargetScope nvarchar(300) NULL,
                    DeploymentName nvarchar(64) NOT NULL,
                    ApprovedBy nvarchar(320) NOT NULL,
                    ApprovedByUserId nvarchar(64) NOT NULL,
                    ApprovedAtUtc datetime2 NOT NULL,
                    PreviewEvidence nvarchar(64) NULL,
                    RevertPlanHash nvarchar(71) NULL,
                    RevertPlanJson nvarchar(max) NULL,
                    EventType nvarchar(16) NOT NULL,
                    Detail nvarchar(1024) NULL,
                    TimestampUtc datetime2 NOT NULL);
                IF OBJECT_ID(N'AdsPlanApplyEvents', N'U') IS NOT NULL AND INDEXPROPERTY(OBJECT_ID(N'AdsPlanApplyEvents'), N'IX_AdsPlanApplyEvents_ApplyId', 'IndexID') IS NULL
                CREATE INDEX IX_AdsPlanApplyEvents_ApplyId ON AdsPlanApplyEvents (ApplyId);
                IF OBJECT_ID(N'AdsPlanApprovals', N'U') IS NOT NULL AND COL_LENGTH(N'AdsPlanApprovals', N'RevertPlanJson') IS NULL
                ALTER TABLE AdsPlanApprovals ADD RevertPlanJson nvarchar(max) NULL;
                IF OBJECT_ID(N'AdsPlanApplyEvents', N'U') IS NOT NULL AND COL_LENGTH(N'AdsPlanApplyEvents', N'RevertPlanHash') IS NULL
                ALTER TABLE AdsPlanApplyEvents ADD RevertPlanHash nvarchar(71) NULL;
                IF OBJECT_ID(N'AdsPlanApplyEvents', N'U') IS NOT NULL AND COL_LENGTH(N'AdsPlanApplyEvents', N'RevertPlanJson') IS NULL
                ALTER TABLE AdsPlanApplyEvents ADD RevertPlanJson nvarchar(max) NULL;
                IF OBJECT_ID(N'AdsPlanApplyEvents', N'U') IS NOT NULL AND COL_LENGTH(N'AdsPlanApplyEvents', N'TargetScope') IS NULL
                ALTER TABLE AdsPlanApplyEvents ADD TargetScope nvarchar(300) NULL;
                IF OBJECT_ID(N'AdsGuardrailPolicies', N'U') IS NULL
                CREATE TABLE AdsGuardrailPolicies (
                    Id int NOT NULL PRIMARY KEY,
                    Json nvarchar(max) NOT NULL,
                    UpdatedBy nvarchar(320) NOT NULL,
                    UpdatedByUserId nvarchar(64) NOT NULL,
                    UpdatedAtUtc datetime2 NOT NULL);
                IF OBJECT_ID(N'AdsDecomApprovals', N'U') IS NULL
                CREATE TABLE AdsDecomApprovals (
                    ExecutionHash nvarchar(71) NOT NULL PRIMARY KEY,
                    WindowId nvarchar(64) NOT NULL,
                    PlanHash nvarchar(71) NOT NULL,
                    SubscriptionId nvarchar(64) NOT NULL,
                    ApprovedBy nvarchar(320) NOT NULL,
                    ApprovedByUserId nvarchar(64) NOT NULL,
                    ApprovedAtUtc datetime2 NOT NULL);
                IF OBJECT_ID(N'AdsDecomWindowEvents', N'U') IS NULL
                CREATE TABLE AdsDecomWindowEvents (
                    Id uniqueidentifier NOT NULL PRIMARY KEY,
                    ApplyId uniqueidentifier NOT NULL,
                    Event nvarchar(16) NOT NULL,
                    WindowId nvarchar(64) NOT NULL,
                    PlanHash nvarchar(71) NOT NULL,
                    ExecutionHash nvarchar(71) NOT NULL,
                    SubscriptionId nvarchar(64) NOT NULL,
                    Principal nvarchar(320) NOT NULL,
                    Detail nvarchar(1024) NULL,
                    AtUtc datetime2 NOT NULL);
                IF OBJECT_ID(N'AdsDecomWindowEvents', N'U') IS NOT NULL AND INDEXPROPERTY(OBJECT_ID(N'AdsDecomWindowEvents'), N'IX_AdsDecomWindowEvents_ApplyId', 'IndexID') IS NULL
                CREATE INDEX IX_AdsDecomWindowEvents_ApplyId ON AdsDecomWindowEvents (ApplyId);
                """);
        }
    }
#endif
}

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
app.MapGrpcService<DesignService>().EnableGrpcWeb();
app.MapGrpcService<DeployService>().EnableGrpcWeb();
app.MapGrpcService<RetailPriceService>().EnableGrpcWeb();

#if DEBUG
if (app.Environment.IsDevelopment())
{
    app.MapGrpcReflectionService();
}
#endif

app.MapStaticAssets();
app.MapRazorPages();
app.MapFallbackToFile("index.html");

app.Run();
