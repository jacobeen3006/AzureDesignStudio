using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using AzureDesignStudio.Core.Common;
using AzureDesignStudio.Discovery.Arm;
using AzureDesignStudio.Discovery.Naming;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// v2 plan 10.4 online half: the live availability client POSTs only the verified
/// checkNameAvailability shapes (URL, body and response-key per family), never invents a
/// verdict - uncheckable families produce no HTTP call, and a response without a readable
/// boolean is an Error, never a fabricated "available".
/// </summary>
public class NameAvailabilityClientTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public List<string> Uris { get; } = [];
        public List<string> Methods { get; } = [];
        public List<string?> Bodies { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!.ToString());
            Methods.Add(request.Method.Method);
            Bodies.Add(request.Content is null ? null : request.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            return Task.FromResult(responder(request));
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new AccessToken("fake-token", DateTimeOffset.MaxValue));
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken)
            => new("fake-token", DateTimeOffset.MaxValue);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static NameAvailabilityClient Client(FakeHandler handler) =>
        new(new ArmTransport(new HttpClient(handler), new FakeCredential(), ArmEndpointSet.AzureCloud));

    [Fact]
    public void Supports_IsTheVerifiedTableExactly_NothingElse()
    {
        // The globally-unique families minus cosmos-db (whose check path could not be verified).
        string[] checkedFamilies =
        [
            AdsConstants.StorageAccount, AdsConstants.SqlServer, AdsConstants.ContainerRegistry,
            AdsConstants.KeyVault, AdsConstants.AiSearch, AdsConstants.SignalR,
            AdsConstants.WebApp, AdsConstants.FunctionApp, AdsConstants.APIM,
        ];
        foreach (var key in checkedFamilies)
            Assert.True(NameAvailabilityClient.Supports(key), key);

        // Every other KnownGloballyUnique family and every unrelated family: not checkable.
        Assert.False(NameAvailabilityClient.Supports(AdsConstants.CosmosDBAccount));
        Assert.False(NameAvailabilityClient.Supports(AdsConstants.VirtualMachine));
        Assert.False(NameAvailabilityClient.Supports(""));
    }

    [Fact]
    public void RequiresLocation_IsSignalROnly()
    {
        Assert.True(NameAvailabilityClient.RequiresLocation(AdsConstants.SignalR));
        Assert.False(NameAvailabilityClient.RequiresLocation(AdsConstants.StorageAccount));
        Assert.False(NameAvailabilityClient.RequiresLocation(AdsConstants.WebApp));
    }

    [Fact]
    public async Task UncheckableFamilies_AnswerNotCheckable_WithZeroHttpRequests()
    {
        var handler = new FakeHandler(_ => Json("{}"));
        var client = Client(handler);

        var cosmos = await client.CheckAsync(AdsConstants.CosmosDBAccount, "db1", "sub-1", "westeurope");
        Assert.Equal(NameCheckOutcome.NotCheckable, cosmos.Outcome);
        Assert.Equal("No live availability check for this family", cosmos.Detail);

        var other = await client.CheckAsync(AdsConstants.VirtualMachine, "vm1", "sub-1", null);
        Assert.Equal(NameCheckOutcome.NotCheckable, other.Outcome);
        Assert.Empty(handler.Uris); // nothing leaves the app for an uncheckable family
    }

    [Fact]
    public async Task SignalR_WithoutRegion_IsNotCheckable_AndSendsNothing()
    {
        var handler = new FakeHandler(_ => Json("{}"));
        var result = await Client(handler).CheckAsync(AdsConstants.SignalR, "sig1", "sub-1", "  ");
        Assert.Equal(NameCheckOutcome.NotCheckable, result.Outcome);
        Assert.Equal("A region is required for this check", result.Detail);
        Assert.Empty(handler.Uris);
    }

    [Fact]
    public async Task Storage_HappyPath_PinsUrlBodyAndReadsNameAvailable()
    {
        var handler = new FakeHandler(_ => Json("""{"nameAvailable":true}"""));

        var result = await Client(handler).CheckAsync(AdsConstants.StorageAccount, "azure", "sub-1", null);

        Assert.Equal(NameCheckOutcome.Available, result.Outcome);
        Assert.Equal("POST", handler.Methods[0]);
        Assert.Equal(
            "https://management.azure.com/subscriptions/sub-1/providers/Microsoft.Storage/checkNameAvailability?api-version=2025-06-01",
            handler.Uris[0]);
        Assert.Equal("""{"name":"azure","type":"Microsoft.Storage/storageAccounts"}""", handler.Bodies[0]);
    }

    [Fact]
    public async Task Sql_UsesAvailableKey_AndComposesReasonAndMessage()
    {
        var handler = new FakeHandler(_ => Json("""{"available":false,"reason":"AlreadyExists","message":"Server name already exists."}"""));

        var result = await Client(handler).CheckAsync(AdsConstants.SqlServer, "sql1", "sub-1", null);

        Assert.Equal(NameCheckOutcome.Unavailable, result.Outcome);
        Assert.Contains("AlreadyExists", result.Detail);
        Assert.Contains("Server name already exists.", result.Detail);
        Assert.Equal(
            "https://management.azure.com/subscriptions/sub-1/providers/Microsoft.Sql/checkNameAvailability?api-version=2023-08-01-preview",
            handler.Uris[0]);
    }

    [Fact]
    public async Task WebAndFunctionApps_UseTheLowercaseWebPath()
    {
        var handler = new FakeHandler(_ => Json("""{"nameAvailable":true}"""));
        var client = Client(handler);

        await client.CheckAsync(AdsConstants.WebApp, "site1", "sub-1", null);
        await client.CheckAsync(AdsConstants.FunctionApp, "func1", "sub-1", null);

        Assert.Equal(
            "https://management.azure.com/subscriptions/sub-1/providers/Microsoft.Web/checknameavailability?api-version=2024-11-01",
            handler.Uris[0]);
        Assert.Equal(handler.Uris[0], handler.Uris[1]);
        Assert.Equal("""{"name":"site1","type":"Microsoft.Web/sites"}""", handler.Bodies[0]);
    }

    [Fact]
    public async Task Apim_BodyCarriesOnlyTheName_SignalRSplicesTheRegion()
    {
        var handler = new FakeHandler(_ => Json("""{"nameAvailable":true}"""));
        var client = Client(handler);

        await client.CheckAsync(AdsConstants.APIM, "api1", "sub-1", null);
        Assert.Equal("""{"name":"api1"}""", handler.Bodies[0]);

        await client.CheckAsync(AdsConstants.SignalR, "sig-1", "sub-1", "West Europe");
        // The region is escaped on the wire; the Uri round-trip decodes it in ToString().
        Assert.Contains("/locations/West Europe/checkNameAvailability?api-version=2024-03-01", handler.Uris[1]);
    }

    [Fact]
    public async Task MissingOrMisTypedFlag_IsError_NeverAFabricatedAvailable()
    {
        var handler = new FakeHandler(_ => Json("""{"nameAvailable":"yes","reason":"AlreadyExists"}"""));
        var misTyped = await Client(handler).CheckAsync(AdsConstants.StorageAccount, "azure", "sub-1", null);
        Assert.Equal(NameCheckOutcome.Error, misTyped.Outcome);
        Assert.Contains("no readable availability flag", misTyped.Detail);

        handler = new FakeHandler(_ => Json("""{"reason":"Invalid"}"""));
        var missing = await Client(handler).CheckAsync(AdsConstants.KeyVault, "kv1", "sub-1", null);
        Assert.Equal(NameCheckOutcome.Error, missing.Outcome);
    }

    [Fact]
    public async Task TransportRefusal_NamesTheStatus_AndNeverClaimsAvailability()
    {
        var handler = new FakeHandler(_ => Json("""{"error":{"code":"AuthorizationFailed","message":"nope"}}""", HttpStatusCode.Forbidden));

        var result = await Client(handler).CheckAsync(AdsConstants.ContainerRegistry, "acr1", "sub-1", null);

        Assert.Equal(NameCheckOutcome.Error, result.Outcome);
        Assert.Contains("HTTP 403", result.Detail);
        Assert.Contains("AuthorizationFailed", result.Detail);
    }

    [Fact]
    public async Task FlagLookupIsCaseInsensitive_AndUnavailableWithoutStringsKeepsNullDetail()
    {
        var handler = new FakeHandler(_ => Json("""{"NameAvailable":true}"""));
        Assert.Equal(NameCheckOutcome.Available, (await Client(handler).CheckAsync(AdsConstants.AiSearch, "srch", "sub-1", null)).Outcome);

        handler = new FakeHandler(_ => Json("""{"nameAvailable":false}"""));
        var bare = await Client(handler).CheckAsync(AdsConstants.AiSearch, "srch", "sub-1", null);
        Assert.Equal(NameCheckOutcome.Unavailable, bare.Outcome);
        Assert.Null(bare.Detail);
    }
}
