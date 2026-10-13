using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;
using AzureDesignStudio.Discovery.ResourceGraph;

namespace AzureDesignStudio.Core.Tests;

/// <summary>
/// ARG query spine (v2 plan 7.1): pagination, Retry-After/quota backoff, bounded retries,
/// concurrency cap - replayed against a scripted HTTP handler, no network.
/// </summary>
public class ResourceGraphTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, int, HttpResponseMessage> _responder;
        public ConcurrentQueue<string> RequestBodies { get; } = new();

        public FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> responder) => _responder = responder;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            RequestBodies.Enqueue(body);
            return _responder(request, RequestBodies.Count - 1);
        }
    }

    private sealed class FakeCredential : TokenCredential
    {
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext context, CancellationToken cancellationToken)
            => ValueTask.FromResult(new AccessToken("fake-token", DateTimeOffset.MaxValue));
        public override AccessToken GetToken(TokenRequestContext context, CancellationToken cancellationToken)
            => new("fake-token", DateTimeOffset.MaxValue);
    }

    private static HttpResponseMessage ArgJson(string json, HttpStatusCode status = HttpStatusCode.OK,
        IEnumerable<(string, string)>? headers = null, TimeSpan? retryAfter = null)
    {
        var response = new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (headers is not null)
            foreach (var (name, value) in headers) response.Headers.TryAddWithoutValidation(name, value);
        if (retryAfter is { } ra) response.Headers.RetryAfter = new RetryConditionHeaderValue(ra);
        return response;
    }

    private static string Page(int rows, bool truncated, string? skipToken = null, long? total = null)
    {
        var data = string.Join(",", Enumerable.Range(0, rows).Select(i => $$"""{"id":"/subs/s/rg/r/providers/Microsoft.Test/t/n{{i}}"}"""));
        var token = skipToken is null ? "" : """ "$skipToken": """ + skipToken + ",";
        return $$"""{"data":[{{data}}],"count":{{rows}},"totalRecords":{{total ?? rows}},{{token}}"resultTruncated":"{{(truncated ? "true" : "false")}}"}""";
    }

    private static (ResourceGraphClient Client, FakeHandler Handler, List<TimeSpan> Delays) Create(
        Func<HttpRequestMessage, int, HttpResponseMessage> responder, ResourceGraphOptions? options = null)
    {
        var handler = new FakeHandler(responder);
        var delays = new List<TimeSpan>();
        var client = new ResourceGraphClient(new HttpClient(handler), new FakeCredential(), options,
            (ts, _) => { delays.Add(ts); return Task.CompletedTask; });
        return (client, handler, delays);
    }

    [Fact]
    public async Task QueryAllAsync_FollowsSkipTokenAcrossPages()
    {
        var (client, handler, _) = Create((_, i) => i switch
        {
            0 => ArgJson(Page(1000, truncated: true, skipToken: """{"min":1}""", total: 2500)),
            1 => ArgJson(Page(1000, truncated: true, skipToken: """{"min":1001}""", total: 2500)),
            _ => ArgJson(Page(500, truncated: false, total: 2500)),
        });

        var result = await client.QueryAllAsync("Resources | project id");

        Assert.Equal(2500, result.Rows.Count);
        Assert.Equal(2500, result.TotalRecords);
        Assert.False(result.Truncated);
        Assert.Equal(3, handler.RequestBodies.Count);
        // page 2+ must carry the token the server handed back
        using var second = JsonDocument.Parse(handler.RequestBodies.ToArray()[1]);
        Assert.Equal(1, second.RootElement.GetProperty("options").GetProperty("$skipToken").GetProperty("min").GetInt32());
    }

    [Fact]
    public async Task QueryAllAsync_TruncatedWithoutToken_StopsAndReports()
    {
        var (client, handler, _) = Create((_, _) => ArgJson(Page(1000, truncated: true))); // no $skipToken

        var result = await client.QueryAllAsync("Resources | take 5000");

        Assert.Equal(1000, result.Rows.Count);
        Assert.True(result.Truncated);
        Assert.Single(handler.RequestBodies); // no infinite loop
    }

    [Fact]
    public async Task QueryPageAsync_429WithRetryAfter_WaitsAdvertisedDelay()
    {
        var (client, _, delays) = Create((_, i) => i switch
        {
            0 => ArgJson("{}", HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromSeconds(3)),
            _ => ArgJson(Page(2, truncated: false)),
        });

        var page = await client.QueryPageAsync("Resources | count");

        Assert.Equal(2, page.Rows.Count);
        Assert.Equal(TimeSpan.FromSeconds(3), delays.Single());
    }

    [Fact]
    public async Task QueryPageAsync_429WithoutHeader_ExhaustsBoundedRetryBudget()
    {
        var (client, _, delays) = Create(
            (_, _) => ArgJson("{}", HttpStatusCode.TooManyRequests),
            new ResourceGraphOptions { MaxThrottleRetries = 4 });

        var error = await Assert.ThrowsAsync<ResourceGraphException>(() => client.QueryPageAsync("x"));
        Assert.True(error.Throttled);
        Assert.Equal(4, delays.Count); // retry budget exhausted
        Assert.True(delays.All(d => d <= TimeSpan.FromSeconds(31)), $"backoff ceiling exceeded: {delays.Max()}");
    }

    [Fact]
    public async Task QueryPageAsync_QuotaExhausted_WaitsForWindowResetBeforeNextCall()
    {
        var (client, _, delays) = Create((_, _) => ArgJson(
            Page(1, truncated: false),
            headers: new[] { ("x-ms-user-quota-remaining", "0"), ("x-ms-user-quota-resets-after", "00:00:05") }));

        await client.QueryPageAsync("x");           // observes quota == 0
        await client.QueryPageAsync("x");           // must wait first

        var waited = delays.Single();
        Assert.InRange(waited, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6)); // reset + jitter
    }

    [Fact]
    public async Task QueryPageAsync_ErrorBodySurfacesServiceCode()
    {
        var (client, _, _) = Create((_, _) => ArgJson(
            """{"error":{"code":"AuthorizationFailed","message":"The client doesn't have access."}}""",
            HttpStatusCode.Forbidden));

        var error = await Assert.ThrowsAsync<ResourceGraphException>(() => client.QueryPageAsync("x"));
        Assert.Equal("AuthorizationFailed", error.Code);
        Assert.Equal(403, error.StatusCode);
    }

    [Fact]
    public async Task Client_NeverExceedsMaxConcurrency()
    {
        var maxInflight = 0;
        int inflight = 0;
        var release = new SemaphoreSlim(0);
        var handler = new CountingHandler(async req =>
        {
            var now = Interlocked.Increment(ref inflight);
            lock (release) maxInflight = Math.Max(maxInflight, now);
            await release.WaitAsync();
            Interlocked.Decrement(ref inflight);
            return ArgJson(Page(1, truncated: false));
        });
        var client = new ResourceGraphClient(new HttpClient(handler), new FakeCredential(),
            new ResourceGraphOptions { MaxConcurrency = 2 });

        var queries = Enumerable.Range(0, 8).Select(_ => client.QueryPageAsync("x")).ToArray();
        var all = Task.WhenAll(queries);
        while (!all.IsCompleted)
        {
            await Task.Delay(10);
            release.Release(2); // keep at most 2 unblocked regardless of caller pressure
        }

        Assert.True(maxInflight <= 2, $"concurrency breach: {maxInflight} in flight");
        Assert.Equal(8, handler.Served);
    }

    private sealed class CountingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder) : HttpMessageHandler
    {
        public int Served;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Served);
            return responder(request);
        }
    }

    [Fact]
    public void PageParse_AcceptsBoolAndStringResultTruncated()
    {
        Assert.True(ResourceQueryPage.Parse("""{"data":[],"resultTruncated":"True"}""").ResultTruncated);
        Assert.True(ResourceQueryPage.Parse("""{"data":[],"resultTruncated":true}""").ResultTruncated);
        Assert.False(ResourceQueryPage.Parse("""{"data":[],"resultTruncated":false}""").ResultTruncated);
    }
}
