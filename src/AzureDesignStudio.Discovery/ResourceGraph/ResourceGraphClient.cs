using System.Net;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Azure.Core;

namespace AzureDesignStudio.Discovery.ResourceGraph;

/// <summary>
/// The single Azure Resource Graph query engine (v2 plan 7.1). One engine for every collector:
/// 1k-row pagination via <c>$skipToken</c>, ARG quota-header backoff, ARM 429/Retry-After
/// backoff, and a concurrency gate so parallel collectors cannot burst the quota window.
/// Endpoint/scope are configurable for sovereign clouds (7.6).
/// </summary>
public sealed class ResourceGraphClient
{
    private readonly HttpClient _http;
    private readonly TokenCredential _credential;
    private readonly ResourceGraphOptions _options;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _gate;

    // Last observed quota window (x-ms-user-quota-*); shared across callers via the gate.
    private int _quotaRemaining = int.MaxValue;
    private TimeSpan _quotaReset = TimeSpan.Zero;

    public ResourceGraphClient(
        HttpClient http,
        TokenCredential credential,
        ResourceGraphOptions? options = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _credential = credential;
        _options = options ?? new ResourceGraphOptions();
        _delay = delay ?? Task.Delay;
        _gate = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);
    }

    /// <summary>
    /// Runs <paramref name="query"/> to completion, following <c>$skipToken</c> pages.
    /// Rows are raw ARM objects - the typeless inventory layer (7.4) consumes them without
    /// per-type code. Throws <see cref="ResourceGraphException"/> on auth/query/limit errors
    /// and after bounded retry budget on throttling.
    /// </summary>
    public async Task<ResourceQueryResult> QueryAllAsync(
        string query,
        IReadOnlyList<string>? subscriptions = null,
        CancellationToken ct = default)
    {
        var rows = new List<JsonElement>();
        JsonNode? skipToken = null;
        long totalRecords = 0;
        var truncated = false;

        while (true)
        {
            var page = await QueryPageAsync(query, subscriptions, skipToken, ct).ConfigureAwait(false);
            foreach (var row in page.Rows) rows.Add(row);
            totalRecords = page.TotalRecords;

            if (!page.ResultTruncated) break;
            if (page.SkipToken is null)
            {
                // Truncated but unpageable (limit/sample operators or all-dynamic columns):
                // hard cap reached - report it instead of looping forever.
                truncated = true;
                break;
            }
            skipToken = page.SkipToken; // independent node from Parse; safe to carry forward
        }

        return new ResourceQueryResult(rows, totalRecords, truncated);
    }

    /// <summary>Single page; exposed for collectors that manage their own paging.</summary>
    public async Task<ResourceQueryPage> QueryPageAsync(
        string query,
        IReadOnlyList<string>? subscriptions = null,
        JsonNode? skipToken = null,
        CancellationToken ct = default)
    {
        var request = new JsonObject
        {
            ["query"] = query,
            ["options"] = new JsonObject
            {
                ["resultFormat"] = "objectArray",
                ["$top"] = _options.PageSize,
            },
        };
        if (subscriptions is { Count: > 0 })
            request["subscriptions"] = new JsonArray([.. subscriptions.Select(s => JsonValue.Create(s))]);
        if (skipToken is not null)
            request["options"]!["$skipToken"] = skipToken.DeepClone(); // detach: a node has one parent

        for (var attempt = 0; ; attempt++)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await WaitForQuotaAsync(ct).ConfigureAwait(false);

                using var message = BuildRequest(request, ct);
                using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct)
                    .ConfigureAwait(false);

                ObserveQuota(response);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); // drain
                    var backoff = RetryAfter(response) ?? ExponentialBackoff(attempt);
                    if (attempt >= _options.MaxThrottleRetries)
                        throw new ResourceGraphException("RateLimiting",
                            $"Resource Graph throttled; retry budget ({_options.MaxThrottleRetries}) exhausted.", throttled: true);
                    await _delay(backoff, ct).ConfigureAwait(false);
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw ResourceGraphException.FromErrorBody((int)response.StatusCode, body);

                return ResourceQueryPage.Parse(body);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private HttpRequestMessage BuildRequest(JsonObject request, CancellationToken ct)
    {
        var token = GetToken(ct);
        var message = new HttpRequestMessage(HttpMethod.Post,
            $"{_options.Endpoint.TrimEnd('/')}/providers/Microsoft.ResourceGraph/resources?api-version={_options.ApiVersion}")
        {
            Content = new StringContent(request.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        message.Headers.Add("x-ms-request-id", Guid.NewGuid().ToString());
        return message;
    }

    private string GetToken(CancellationToken ct)
    {
        // TokenCredential is sync-hostile; request with infinite cancellation - token fetch itself
        // never honors our query cancellation and a token cache hit completes synchronously.
        var value = _credential.GetTokenAsync(
            new TokenRequestContext(new[] { _options.Scope }), CancellationToken.None).GetAwaiter().GetResult();
        return value.Token;
    }

    private void ObserveQuota(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("x-ms-user-quota-remaining", out var remaining)
            && int.TryParse(remaining.FirstOrDefault(), out var r))
            _quotaRemaining = r;
        if (response.Headers.TryGetValues("x-ms-user-quota-resets-after", out var reset)
            && TimeSpan.TryParse(reset.FirstOrDefault(), out var t))
            _quotaReset = t;
    }

    private async Task WaitForQuotaAsync(CancellationToken ct)
    {
        // Doc'd pattern: when the window is spent, wait for the reset before the next call.
        if (_quotaRemaining > 0 || _quotaReset <= TimeSpan.Zero) return;
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.Next(0, 250));
        await _delay(_quotaReset + jitter, ct).ConfigureAwait(false);
        _quotaRemaining = 1; // one request may go through after the reset; the header will correct us
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.RetryAfter is { } ra)
            return ra.Delta ?? (ra.Date is { } date ? date - DateTimeOffset.UtcNow : null);
        return null;
    }

    private TimeSpan ExponentialBackoff(int attempt)
    {
        var seconds = Math.Min(30, Math.Pow(2, attempt)); // 1,2,4,8,16,30,30...
        return TimeSpan.FromSeconds(seconds) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 500));
    }
}
