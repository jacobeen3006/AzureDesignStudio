using AzureDesignStudio;
using AzureDesignStudio.Core.DTO;
using AzureDesignStudio.Services;
using AzureDesignStudio.SharedModels.Protos;
using BlazorApplicationInsights;
using Grpc.Net.Client.Web;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Authentication;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

builder.Services.AddHttpClient("AzureDesignStudio.Root",
    client => client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress));
// ResourceRoot may be a root-relative path (dev: "/"); resolve it against the host base
// because new Uri("/") on WASM parses as file:/// and breaks HttpClient request URIs.
var resourceRoot = builder.Configuration.GetValue<string>("ResourceRoot")!;
var resourceBase = new Uri(resourceRoot, UriKind.RelativeOrAbsolute);
if (!resourceBase.IsAbsoluteUri || resourceBase.Scheme == Uri.UriSchemeFile)
{
    var hostAuthority = new Uri(builder.HostEnvironment.BaseAddress).GetLeftPart(UriPartial.Authority);
    resourceBase = new Uri(resourceRoot.StartsWith('/') ? hostAuthority + resourceRoot : hostAuthority + "/" + resourceRoot);
}
builder.Services.AddHttpClient("AzureDesignStudio.ResourceAccess",
    client => client.BaseAddress = resourceBase);
// Microsoft Entra ID Authentication (work + personal accounts)
builder.Services.AddHttpClient("AzureDesignStudio.ServerAPI", client => client.BaseAddress = new Uri(builder.HostEnvironment.BaseAddress))
    .AddHttpMessageHandler<BaseAddressAuthorizationMessageHandler>();
builder.Services.AddScoped(sp => sp.GetRequiredService<IHttpClientFactory>().CreateClient("AzureDesignStudio.ServerAPI"));
builder.Services.AddMsalAuthentication(options =>
{
    builder.Configuration.Bind("AzureAd", options.ProviderOptions.Authentication);
    options.ProviderOptions.DefaultAccessTokenScopes.Add(builder.Configuration.GetValue<string>("ServerScope")!);
});

builder.Logging.AddConfiguration(builder.Configuration.GetSection("Logging"));
// App insights
builder.Services.AddBlazorApplicationInsights();
// Grpc
builder.Services.AddScoped<DesignGrpcService>();
builder.Services.AddGrpcClient<Design.DesignClient>("DesignClientWithAuth", o =>
{
    o.Address = new Uri(builder.HostEnvironment.BaseAddress);
}).ConfigurePrimaryHttpMessageHandler(() =>
{
    var baseAddressMessageHandler = builder.Services.BuildServiceProvider().GetRequiredService<BaseAddressAuthorizationMessageHandler>();
    baseAddressMessageHandler.InnerHandler = new HttpClientHandler();
    return new GrpcWebHandler(GrpcWebMode.GrpcWeb, baseAddressMessageHandler);
});
builder.Services.AddScoped<DeployGrpcService>();
builder.Services.AddGrpcClient<Deploy.DeployClient>("DeployClientWithAuth", o =>
{
    o.Address = new Uri(builder.HostEnvironment.BaseAddress);
}).ConfigurePrimaryHttpMessageHandler(() =>
{
    var baseAddressMessageHandler = builder.Services.BuildServiceProvider().GetRequiredService<BaseAddressAuthorizationMessageHandler>();
    baseAddressMessageHandler.InnerHandler = new HttpClientHandler();
    return new GrpcWebHandler(GrpcWebMode.GrpcWeb, baseAddressMessageHandler);
});
builder.Services.AddScoped<CostGrpcService>();
builder.Services.AddScoped<CostService>();
builder.Services.AddGrpcClient<Cost.CostClient>("CostClient", o =>
{
    o.Address = new Uri(builder.HostEnvironment.BaseAddress);
}).ConfigurePrimaryHttpMessageHandler(() =>
    new GrpcWebHandler(GrpcWebMode.GrpcWeb, new HttpClientHandler()));

builder.Services.AddAntDesign();
builder.Services.AddSingleton<AdsContext>();
builder.Services.AddSingleton<AzureDesignStudio.Services.AdsCommandService>();
// Server redirect sign-in flow (AddMsalAuthentication + /authentication endpoints);
// no host overrides needed on WASM.
builder.Services.AddSingleton<AdsHostAuth>();
// Browser host has no tenant scan; desktop replaces this registration with its own.
builder.Services.AddSingleton<AdsHostFeatures>();
builder.Services.AddAutoMapper(typeof(AzureNodeProfile));
// Bicep decompiler service
builder.Services.AddAdsBicepDecompiler();

var host = builder.Build();

var adsContext = host.Services.GetRequiredService<AdsContext>();
await adsContext.InitializeAsync();

await host.RunAsync();
