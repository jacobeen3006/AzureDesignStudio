using System.Security.Claims;
using AzureDesignStudio.Server.Services;
using AzureDesignStudio.Server.Utils;
using AzureDesignStudio.SharedModels.Protos;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AzureDesignStudio.Server.Tests;

public class DesktopGrpcEndpointTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "test";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    [Fact]
    public void ListenerIsOnInDevelopmentAndOffWhenDeployed()
    {
        Assert.Equal(5204, DesktopGrpcEndpoint.ResolvePort(Config(), new Env("Development")));
        Assert.Equal(0, DesktopGrpcEndpoint.ResolvePort(Config(), new Env("Production")));
    }

    [Fact]
    public void ConfiguredPortWinsEitherWay()
    {
        Assert.Equal(6000, DesktopGrpcEndpoint.ResolvePort(Config((DesktopGrpcEndpoint.PortKey, "6000")), new Env("Production")));
        Assert.Equal(0, DesktopGrpcEndpoint.ResolvePort(Config((DesktopGrpcEndpoint.PortKey, "0")), new Env("Development")));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData("70000")]
    public void PortThatIsNotAPortStopsStartupWithAReason(string value)
    {
        var failure = Assert.Throws<InvalidOperationException>(
            () => DesktopGrpcEndpoint.ResolvePort(Config((DesktopGrpcEndpoint.PortKey, value)), new Env("Development")));

        Assert.Contains(DesktopGrpcEndpoint.PortKey, failure.Message);
    }

    // Defining the Desktop listener in code makes Kestrel drop every URL-configured endpoint,
    // so these are the addresses that have to be bound again next to it.

    [Fact]
    public void HostingUrlsAreKept()
    {
        var addresses = DesktopGrpcEndpoint.HostingAddresses(Config(("urls", "https://localhost:7203;http://localhost:5203")));

        Assert.Equal(new[] { ("https", 7203), ("http", 5203) }, addresses.Select(a => (a.Scheme, a.Port)));
    }

    [Fact]
    public void ContainerPortShorthandIsKept()
    {
        // ASPNETCORE_HTTP_PORTS=80 is how the image is reached; without it nothing listened on 80.
        var address = Assert.Single(DesktopGrpcEndpoint.HostingAddresses(Config(("http_ports", "80"))));

        Assert.Equal(("http", 80, "*"), (address.Scheme, address.Port, address.Host));
    }

    [Fact]
    public void UrlsTakePrecedenceOverThePortShorthand()
    {
        var address = Assert.Single(DesktopGrpcEndpoint.HostingAddresses(Config(("urls", "http://localhost:5203"), ("http_ports", "80"))));

        Assert.Equal(5203, address.Port);
    }

    [Fact]
    public void NothingConfiguredFallsBackToKestrelsDefault()
    {
        var address = Assert.Single(DesktopGrpcEndpoint.HostingAddresses(Config()));

        Assert.Equal(("http", 5000), (address.Scheme, address.Port));
    }
}

public class ServiceToolsTests
{
    private static ClaimsPrincipal User(params Claim[] claims) => new(new ClaimsIdentity(claims, "test"));

    [Fact]
    public void ObjectIdIsReadFromEitherClaimName()
    {
        var id = Guid.NewGuid().ToString();

        Assert.Equal(id, ServiceTools.GetUserId(User(new Claim("http://schemas.microsoft.com/identity/claims/objectidentifier", id))));
        Assert.Equal(id, ServiceTools.GetUserId(User(new Claim("oid", id))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("12345")]
    public void ClaimThatIsNotAGuidReadsAsNoIdentity(string value)
    {
        Assert.Null(ServiceTools.GetUserId(User(new Claim("oid", value))));
    }

    [Fact]
    public void NoClaimReadsAsNoIdentity() => Assert.Null(ServiceTools.GetUserId(User()));

    [Theory]
    [InlineData("hub and spoke 2", true)]
    [InlineData("", false)]
    [InlineData("a:b", false)]
    [InlineData("../etc", false)]
    public void DesignNames(string name, bool valid) => Assert.Equal(valid, ServiceTools.IsValidName(name));

    [Fact]
    public void DesignNameLengthIsCapped() => Assert.False(ServiceTools.IsValidName(new string('a', 201)));

    [Theory]
    [InlineData("<kv name>", false)] // the checked-in appsettings.json: a vault URL built from it crashed startup
    [InlineData("<connection string>", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("ads-kv", true)]
    [InlineData("Server=tcp:sql;Database=ads", true)]
    public void PlaceholderSettingsAreNotConfiguration(string? value, bool configured) =>
        Assert.Equal(configured, ServiceTools.IsConfigured(value));
}

public class CryptoServiceTests
{
    [Theory]
    [InlineData("<kv name>", "<key name>")] // the checked-in appsettings.json
    [InlineData("", "")]
    [InlineData("vault", "<key name>")]
    public async Task PlaceholderVaultIsReportedOnFirstUseWithoutCallingAzure(string vault, string key)
    {
        // Construction must not throw: this is a singleton every Deploy RPC depends on.
        var crypto = new CryptoService(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["KeyVaultName"] = vault,
            ["KeyVaultKeyName"] = key,
        }).Build());

        var encrypt = await Assert.ThrowsAsync<InvalidOperationException>(() => crypto.Encrypt("secret"));
        var decrypt = await Assert.ThrowsAsync<InvalidOperationException>(() => crypto.Decrypt("c2VjcmV0"));

        Assert.Contains("Key Vault is not configured", encrypt.Message);
        Assert.Contains("Key Vault is not configured", decrypt.Message);
    }
}

public class RetailPriceServiceTests
{
    private const string Item = """{"serviceName":"Virtual Machines","skuName":"D2 v5","meterName":"D2 v5","productName":"p","unitOfMeasure":"1 Hour","retailPrice":0.096,"currencyCode":"USD","armRegionName":"westeurope"}""";

    private static RetailPriceService Service(ScriptedHttp http) =>
        new(http, new MemoryCache(new MemoryCacheOptions()), NullLogger<RetailPriceService>.Instance);

    // Each test asks for its own region so the per-query cache of one cannot answer another.
    private static RetailPriceRequest Request(string region) => new() { ServiceName = "Virtual Machines", ArmRegionName = region };

    [Fact]
    public async Task PagesAreFollowedOnThePricesHost()
    {
        var http = new ScriptedHttp(uri => uri.Query.Contains("skip=1")
            ? "{\"Items\":[" + Item + "],\"NextPageLink\":null}"
            : "{\"Items\":[" + Item + "],\"NextPageLink\":\"https://prices.azure.com/api/retail/prices?skip=1\"}");

        var response = await Service(http).GetRetailPrices(Request("region-a"), TestServerCallContext.WithoutIdentity());

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(2, response.Prices.Count);
        Assert.Equal(0.096, response.Prices[0].RetailPrice);
        Assert.Equal(2, http.Requests.Count);
    }

    [Fact]
    public async Task NextPageLinkToAnotherHostIsNotFollowed()
    {
        var http = new ScriptedHttp(_ => "{\"Items\":[" + Item + "],\"NextPageLink\":\"https://example.invalid/steal\"}");

        var response = await Service(http).GetRetailPrices(Request("region-b"), TestServerCallContext.WithoutIdentity());

        Assert.Equal(200, response.StatusCode);
        Assert.Single(response.Prices);
        Assert.All(http.Requests, uri => Assert.Equal("prices.azure.com", uri.Host));
    }

    [Fact]
    public async Task CompleteAnswerIsCachedAndATruncatedOneIsNot()
    {
        var complete = new ScriptedHttp(_ => "{\"Items\":[" + Item + "]}");
        var completeService = Service(complete);
        await completeService.GetRetailPrices(Request("region-c"), TestServerCallContext.WithoutIdentity());
        await completeService.GetRetailPrices(Request("region-c"), TestServerCallContext.WithoutIdentity());
        Assert.Single(complete.Requests);

        // Cut off (the next page is elsewhere): cached, it would pose as the whole list for an hour.
        var truncated = new ScriptedHttp(_ => "{\"Items\":[" + Item + "],\"NextPageLink\":\"https://example.invalid/next\"}");
        var truncatedService = Service(truncated);
        await truncatedService.GetRetailPrices(Request("region-d"), TestServerCallContext.WithoutIdentity());
        await truncatedService.GetRetailPrices(Request("region-d"), TestServerCallContext.WithoutIdentity());
        Assert.Equal(2, truncated.Requests.Count);
    }

    [Fact]
    public async Task UpstreamFailureIsABadGatewayNotAnException()
    {
        var response = await Service(new ScriptedHttp(_ => null)).GetRetailPrices(Request("region-e"), TestServerCallContext.WithoutIdentity());

        Assert.Equal(502, response.StatusCode);
        Assert.Empty(response.Prices);
    }

    [Fact]
    public async Task ImplausibleInputNeverReachesUpstream()
    {
        var http = new ScriptedHttp(_ => "{\"Items\":[]}");
        var service = Service(http);
        var requests = new[]
        {
            new RetailPriceRequest { ServiceName = "", ArmRegionName = "westeurope" },
            new RetailPriceRequest { ServiceName = "Virtual Machines", ArmRegionName = "" },
            new RetailPriceRequest { ServiceName = new string('x', 500), ArmRegionName = "westeurope" },
            new RetailPriceRequest { ServiceName = "Virtual\nMachines", ArmRegionName = "westeurope" },
            new RetailPriceRequest { ServiceName = "Virtual Machines", ArmRegionName = "westeurope", CurrencyCode = "US$" },
        };

        foreach (var request in requests)
            Assert.Equal(400, (await service.GetRetailPrices(request, TestServerCallContext.WithoutIdentity())).StatusCode);

        Assert.Empty(http.Requests);
    }

    [Fact]
    public void QuoteInAFilterValueCannotBreakOutOfTheFilter()
    {
        var url = RetailPriceService.BuildRequestUrl(new RetailPriceRequest { ServiceName = "x' or 1 eq 1 or 'a' eq 'a", ArmRegionName = "westeurope" });

        var filter = Uri.UnescapeDataString(url[(url.IndexOf("$filter=", StringComparison.Ordinal) + 8)..]);
        Assert.StartsWith("serviceName eq 'x'' or 1 eq 1 or ''a'' eq ''a' and armRegionName eq 'westeurope'", filter);
    }
}
