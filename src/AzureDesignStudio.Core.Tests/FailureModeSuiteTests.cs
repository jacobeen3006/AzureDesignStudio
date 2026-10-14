using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using AzureDesignStudio.Discovery.Arm;
using AzureDesignStudio.Discovery.Preflight;
using AzureDesignStudio.Discovery.ResourceGraph;
using Xunit;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// Failure-mode suite (v2 plan 9.7, offline half): throttle, partial permissions,
/// offline and malformed input replayed against scripted handlers - each failure must
/// land in the right user-facing bucket (caveat vs not-available vs typed throw),
/// never a hang, silent empty result or unclassified crash.
/// </summary>
public class FailureModeSuiteTests
{
    private sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public ConcurrentQueue<string> Uris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Enqueue(request.RequestUri!.ToString());
            return Task.FromResult(responder(request, Uris.Count - 1));
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new AccessToken("fake-token", DateTimeOffset.MaxValue));
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken)
            => new("fake-token", DateTimeOffset.MaxValue);
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (retryAfter is { } ra) response.Headers.RetryAfter = new RetryConditionHeaderValue(ra);
        return response;
    }

    private static HttpResponseMessage Accepted(string location)
    {
        var response = Json("", HttpStatusCode.Accepted);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private static (ArmTransport Transport, FakeHandler Handler, List<TimeSpan> Delays) CreateTransport(
        Func<HttpRequestMessage, int, HttpResponseMessage> responder, int maxThrottleRetries = 5)
    {
        var handler = new FakeHandler(responder);
        var delays = new List<TimeSpan>();
        var transport = new ArmTransport(new HttpClient(handler), new FakeCredential(), ArmEndpointSet.AzureCloud,
            (ts, _) => { delays.Add(ts); return Task.CompletedTask; }, maxThrottleRetries);
        return (transport, handler, delays);
    }

    private static (PermissionPreflight Preflight, FakeHandler Handler) CreatePreflight(
        Func<HttpRequestMessage, int, HttpResponseMessage> responder, int maxThrottleRetries = 5)
    {
        var handler = new FakeHandler(responder);
        var http = new HttpClient(handler);
        var noWait = (TimeSpan _, CancellationToken _) => Task.CompletedTask;
        var transport = new ArmTransport(http, new FakeCredential(), ArmEndpointSet.AzureCloud, noWait, maxThrottleRetries);
        var graph = new ResourceGraphClient(http, new FakeCredential(), delay: noWait);
        return (new PermissionPreflight(transport, graph), handler);
    }

    // --- throttle ---

    [Fact]
    public async Task GetList_429ThenOk_WaitsAdvertisedDelayAndPages()
    {
        var (transport, _, delays) = CreateTransport((_, i) => i switch
        {
            0 => Json("""{"value":[{"id":"/a"}]}""", HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(7)),
            1 => Json("""{"value":[{"id":"/a"}],"nextLink":"https://management.azure.com/next"}"""),
            _ => Json("""{"value":[{"id":"/b"}]}"""),
        });

        var items = await transport.GetListAsync("https://management.azure.com/subscriptions?api-version=x", "value");

        Assert.Equal(2, items.Count); // page joined after backoff
        Assert.Equal(TimeSpan.FromSeconds(7), Assert.Single(delays)); // honoured Retry-After, no hammering
    }

    [Fact]
    public async Task GetList_ThrottleBudgetExhausted_ThrowsTypedThrottled()
    {
        var (transport, handler, _) = CreateTransport((_, _) => Json("{}", HttpStatusCode.TooManyRequests), maxThrottleRetries: 2);

        var e = await Assert.ThrowsAsync<ResourceGraphException>(() =>
            transport.GetListAsync("https://management.azure.com/subscriptions?api-version=x", "value"));

        Assert.True(e.Throttled);
        Assert.Equal(429, e.StatusCode);
        Assert.Equal(3, handler.Uris.Count); // bounded: initial + 2 retries, never infinite
    }

    [Fact]
    public async Task LroPoll_ThrottledMidPoll_Recovers()
    {
        var (transport, _, delays) = CreateTransport((req, i) => i switch
        {
            0 => Accepted("https://mgmt/loc"),
            1 => Json("{}", HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(5)),
            _ => Json("""{"done":true}"""),
        });

        var body = await transport.PostAsyncPolled("https://mgmt/query", "{}");

        Assert.Contains("\"done\":true", body);
        Assert.Equal([TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5)], delays); // LRO poll cadence + advertised backoff
    }

    [Fact]
    public async Task Preflight_ThrottledSource_IsCaveatNotMissingPermission()
    {
        var (preflight, _) = CreatePreflight(
            (_, _) => Json("{}", HttpStatusCode.TooManyRequests), maxThrottleRetries: 0);

        var probes = await preflight.RunAsync("sub-1");

        var graph = Assert.Single(probes, p => p.Source == "resourceGraph");
        Assert.Equal(ProbeStatus.AvailableWithCaveat, graph.Status); // throttling is health, not absence
        Assert.DoesNotContain("request role", graph.Detail);
    }

    // --- partial permissions ---

    [Fact]
    public async Task Preflight_OneForbiddenSource_OnlyThatSourceDegrades()
    {
        var (preflight, _) = CreatePreflight((req, _) =>
            req.RequestUri!.ToString().Contains("Microsoft.ResourceGraph")
                ? Json("""{"error":{"code":"AuthorizationFailed","message":"the client does not have access"}}""", HttpStatusCode.Forbidden)
                : Json("""{"value":[]}"""));

        var probes = await preflight.RunAsync("sub-1");

        var graph = Assert.Single(probes, p => p.Source == "resourceGraph");
        Assert.Equal(ProbeStatus.NotAvailable, graph.Status);
        Assert.Contains("AuthorizationFailed", graph.Detail);
        Assert.Contains("Reader", graph.Detail); // actionable: names the role to request

        Assert.Equal(4, probes.Count(p => p.Status == ProbeStatus.Available)); // rest of the app unaffected
    }

    [Fact]
    public async Task Transport_403CarriesServiceErrorCode()
    {
        var (transport, _, _) = CreateTransport((_, _) =>
            Json("""{"error":{"code":"AuthorizationFailed","message":"denied"}}""", HttpStatusCode.Forbidden));

        var e = await Assert.ThrowsAsync<ResourceGraphException>(() => transport.GetAsync("https://management.azure.com/x"));

        Assert.Equal("AuthorizationFailed", e.Code);
        Assert.Equal(403, e.StatusCode);
    }

    // --- offline ---

    [Fact]
    public async Task Transport_Offline_PropagatesHttpRequestExceptionUnwrapped()
    {
        var (transport, _, _) = CreateTransport((_, _) => throw new HttpRequestException("no route to host"));

        await Assert.ThrowsAsync<HttpRequestException>(() => transport.GetAsync("https://management.azure.com/x"));
    }

    [Fact]
    public async Task Preflight_Offline_AllSourcesCaveatNotNotAvailable()
    {
        var (preflight, _) = CreatePreflight((_, _) => throw new HttpRequestException("offline"));

        var probes = await preflight.RunAsync("sub-1");

        Assert.All(probes, p => Assert.Equal(ProbeStatus.AvailableWithCaveat, p.Status)); // offline != permissionless
        Assert.All(probes, p => Assert.Contains("network problem", p.Detail));
    }

    [Fact]
    public async Task Preflight_Cancelled_PropagatesCancellation()
    {
        var (preflight, _) = CreatePreflight((_, _) => Json("""{"value":[]}"""));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preflight.RunAsync("sub-1", cts.Token));
    }

    // --- malformed input ---

    [Fact]
    public async Task GetList_HtmlErrorBodyUnderOk_ThrowsNotSilentlyEmpty()
    {
        var (transport, _, _) = CreateTransport((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>502 from proxy</body></html>"),
        });

        await Assert.ThrowsAnyAsync<JsonException>(() =>
            transport.GetListAsync("https://management.azure.com/subscriptions?api-version=x", "value"));
    }

    [Fact]
    public async Task GetList_MissingValueArray_IsEmptyNotCrash()
    {
        var (transport, _, _) = CreateTransport((_, _) => Json("""{"nextLink":null}"""));

        Assert.Empty(await transport.GetListAsync("https://management.azure.com/x", "value"));
    }

    [Fact]
    public async Task ErrorBody_MisTypedFields_FallBackToStatusNotCrash()
    {
        var (transport, _, _) = CreateTransport((_, _) =>
            Json("""{"error":{"code":403,"message":{"lang":"en","content":"weird"}}}""", HttpStatusCode.Forbidden));

        var e = await Assert.ThrowsAsync<ResourceGraphException>(() => transport.GetAsync("https://management.azure.com/x"));

        Assert.Equal("Unknown", e.Code); // a number where a string belongs must not mask the 403
        Assert.Equal(403, e.StatusCode);
        Assert.Contains("\"code\":403", e.Message); // raw body preserved for triage
    }
}
