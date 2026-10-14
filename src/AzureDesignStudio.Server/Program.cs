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
// URL-configured endpoints and gRPC-Web over HTTP/1.1.
builder.WebHost.ConfigureKestrel(options =>
    options.ListenLocalhost(5204, listen => listen.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http2));

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
    // Use Azure Key Vault as configurations
    var keyVaultName = builder.Configuration["KeyVaultName"];
    if (string.IsNullOrWhiteSpace(keyVaultName))
        throw new InvalidOperationException(
            "KeyVaultName is not configured. Set the KeyVaultName configuration or environment variable (e.g. KeyVaultName=ads-kv).");
    var secretClient = new SecretClient(
        new Uri($"https://{keyVaultName}.vault.azure.net/"),
        new DefaultAzureCredential());

    builder.Configuration.AddAzureKeyVault(secretClient, new KeyVaultSecretManager());
    builder.Services.AddDbContext<DesignDbContext>(options => options.UseSqlServer(builder.Configuration["ads-main"]));
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
    // Full EF schema first: on a database that predates this release, EnsureCreated
    // brings up missing tables; on a brand-new one it creates the database itself
    // (raw DDL alone would fail against a nonexistent database). The guard then makes
    // certain the audit table exists even when the database was created before the
    // approval model joined the model set.
    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<DesignDbContext>();
        if (db.Database.IsRelational())
        {
            db.Database.EnsureCreated();
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
                IF OBJECT_ID(N'AdsGuardrailPolicies', N'U') IS NULL
                CREATE TABLE AdsGuardrailPolicies (
                    Id int NOT NULL PRIMARY KEY,
                    Json nvarchar(max) NOT NULL,
                    UpdatedBy nvarchar(320) NOT NULL,
                    UpdatedByUserId nvarchar(64) NOT NULL,
                    UpdatedAtUtc datetime2 NOT NULL);
                """);
        }
    }
#endif

// Configure the HTTP request pipeline.
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

app.UseForwardedHeaders();
app.UseHttpsRedirection();

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
