using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Azure.Core;

using AzureDesignStudio.Discovery.ResourceGraph;
namespace AzureDesignStudio.Discovery.Arm;

/// <summary>
/// Minimal authenticated ARM/ARG transport shared by every collector: bearer token from the
/// host credential, bounded 429/Retry-After retries, nextLink paging. One HttpClient per host.
/// </summary>
public sealed class ArmTransport(HttpClient http, TokenCredential credential, ArmEndpointSet endpoints,
    Func<TimeSpan, CancellationToken, Task>? delay = null, int maxThrottleRetries = 5)
{
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly SemaphoreSlim _gate = new(4, 4);

    public ArmEndpointSet Endpoints { get; } = endpoints;

    /// <summary>GET a JSON document; follows <c>nextLink</c> when <paramref name="collectValue"/>.</summary>
    public async Task<List<JsonElement>> GetListAsync(string url, string valueProperty, CancellationToken ct = default)
    {
        var items = new List<JsonElement>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? next = url;
        while (next is not null)
        {
            // nextLink comes from the response body and the bearer token goes wherever it
            // points: follow it only on the host the listing started on, and never twice.
            if (!visited.Add(next))
                throw new ResourceGraphException("PagingLoop", $"ARM paging returned the same nextLink twice for {url}");
            if (visited.Count > MaxListPages)
                throw new ResourceGraphException("PagingLimit", $"ARM paging exceeded {MaxListPages} pages for {url}");
            if (visited.Count > 1 && !SameHost(url, next))
                throw new ResourceGraphException("PagingHostChanged", $"ARM paging for {url} pointed at another host; not followed.");

            using var doc = JsonDocument.Parse(await SendAsync(HttpMethod.Get, next, body: null, ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty(valueProperty, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray()) items.Add(item.Clone());

            next = doc.RootElement.TryGetProperty("nextLink", out var link) && link.ValueKind == JsonValueKind.String
                && link.GetString() is { Length: > 0 } nextLink
                ? nextLink
                : null;
        }
        return items;
    }

    private const int MaxListPages = 1000;

    private static bool SameHost(string first, string next) =>
        Uri.TryCreate(first, UriKind.Absolute, out var a)
        && Uri.TryCreate(next, UriKind.Absolute, out var b)
        && string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && a.Port == b.Port;

    public Task<string> GetAsync(string url, CancellationToken ct = default) => SendAsync(HttpMethod.Get, url, null, ct);
    public Task<string> PostAsync(string url, string json, CancellationToken ct = default) => SendAsync(HttpMethod.Post, url, json, ct);
    public Task<string> DeleteAsync(string url, CancellationToken ct = default) => SendAsync(HttpMethod.Delete, url, null, ct);

    /// <summary>POST that follows the LRO shape (202 + <c>Location</c>, poll to completion):
    /// Cost Management queries run as async jobs for anything but tiny windows.</summary>
    public async Task<string> PostAsyncPolled(string url, string json, int maxPolls = 30, CancellationToken ct = default)
    {
        var r = await SendWithThrottleAsync(HttpMethod.Post, url, json, ct).ConfigureAwait(false);
        for (var poll = 0; r.Status == HttpStatusCode.Accepted && r.Location is not null; poll++)
        {
            if (poll >= maxPolls)
                throw new ResourceGraphException("Timeout", $"ARM operation did not finish within {maxPolls} polls: {url}", 408);
            await _delay(r.RetryAfter ?? TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            r = await SendWithThrottleAsync(HttpMethod.Get, r.Location, null, ct).ConfigureAwait(false);
        }

        if (r.IsSuccess) return r.Body;
        throw ResourceGraphException.FromErrorBody((int)r.Status, r.Body);
    }

    /// <summary>PUT that follows the ARM resource-LRO shape: a 201/202 carrying
    /// <c>Azure-AsyncOperation</c> (preferred) or <c>Location</c> polls until a terminal answer
    /// and returns the resource document.
    ///
    /// The two headers poll differently. A <c>Location</c> URL answers 202 until it answers
    /// with the result. An <c>Azure-AsyncOperation</c> URL is a status monitor: it answers
    /// 200 every time, with <c>{"status":"InProgress"}</c> while the operation runs and
    /// Succeeded/Failed/Canceled at the end, and never returns the resource - so once it is
    /// terminal the resource is read from the original URL, whose provisioningState carries
    /// the outcome. (Stopping at the first 200 returned the "InProgress" body as the result.)</summary>
    public async Task<string> PutAsyncPolled(string url, string json, int maxPolls = 60, CancellationToken ct = default)
    {
        var r = await SendWithThrottleAsync(HttpMethod.Put, url, json, ct).ConfigureAwait(false);
        var monitored = r.AsyncOperation is not null;
        var pollUrl = r.AsyncOperation ?? r.Location;
        if (r.Status is not (HttpStatusCode.Accepted or HttpStatusCode.Created) || pollUrl is null)
        {
            if (r.IsSuccess) return r.Body;
            throw ResourceGraphException.FromErrorBody((int)r.Status, r.Body);
        }

        for (var poll = 0; ; poll++)
        {
            if (poll >= maxPolls)
                throw new ResourceGraphException("Timeout", $"ARM operation did not finish within {maxPolls} polls: {url}", 408);
            await _delay(r.RetryAfter ?? TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            r = await SendWithThrottleAsync(HttpMethod.Get, pollUrl, null, ct).ConfigureAwait(false);

            if (r.Status == HttpStatusCode.Accepted)
            {
                // A 202 without operation headers keeps the last poll URI (retry the status endpoint).
                pollUrl = r.AsyncOperation ?? r.Location ?? pollUrl;
                continue;
            }

            if (!r.IsSuccess)
                throw ResourceGraphException.FromErrorBody((int)r.Status, r.Body);

            if (monitored && OperationStatus(r.Body) is { } status)
            {
                if (!IsTerminal(status))
                    continue;
                return await SendAsync(HttpMethod.Get, url, null, ct).ConfigureAwait(false);
            }

            return r.Body;
        }
    }

    /// <summary>The <c>status</c> of an async-operation monitor body, or null when the body is
    /// not one (a monitor that answered with the resource itself, a Location-style result).</summary>
    private static string? OperationStatus(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                ? status.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsTerminal(string status) =>
        status.Equals("Succeeded", StringComparison.OrdinalIgnoreCase)
        || status.Equals("Failed", StringComparison.OrdinalIgnoreCase)
        || status.Equals("Canceled", StringComparison.OrdinalIgnoreCase)
        || status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase);

    private Task<string> SendAsync(HttpMethod method, string url, string? body, CancellationToken ct) =>
        FinishAsync(method, url, body, ct);

    private async Task<string> FinishAsync(HttpMethod method, string url, string? body, CancellationToken ct)
    {
        var r = await SendWithThrottleAsync(method, url, body, ct).ConfigureAwait(false);
        if (r.IsSuccess) return r.Body;
        throw ResourceGraphException.FromErrorBody((int)r.Status, r.Body);
    }

    /// <summary>One logical send with the shared 429/Retry-After budget; never throws on status.</summary>
    private async Task<Response> SendWithThrottleAsync(HttpMethod method, string url, string? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var message = new HttpRequestMessage(method, url);
                // Never block here: on the desktop host this method can be entered
                // on the Blazor UI thread, and the interactive (WAM) credential
                // needs that thread to pump - a sync-over-async token acquire
                // deadlocks it.
                var token = await credential
                    .GetTokenAsync(new TokenRequestContext(new[] { Endpoints.Scope }), ct)
                    .ConfigureAwait(false);
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                if (body is not null) message.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using var response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    if (attempt >= maxThrottleRetries)
                        throw new ResourceGraphException("Throttled", $"ARM throttled; retry budget ({maxThrottleRetries}) exhausted on {url}", 429, throttled: true);
                    var backoff = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt)));
                    await _delay(backoff, ct).ConfigureAwait(false);
                    continue;
                }

                // A 204 carries no body; read nothing (an empty-body read would still work, but
                // skipping it keeps DELETE/204 cheap and uniform with the other success paths).
                var text = response.StatusCode == HttpStatusCode.NoContent
                    ? string.Empty
                    : await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                return new Response(response.StatusCode, text, response.Headers.Location?.ToString(),
                    response.Headers.RetryAfter?.Delta, response.IsSuccessStatusCode,
                    response.Headers.TryGetValues("Azure-AsyncOperation", out var asyncOp) ? asyncOp.FirstOrDefault() : null);
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private sealed record Response(HttpStatusCode Status, string Body, string? Location, TimeSpan? RetryAfter, bool IsSuccess, string? AsyncOperation);
}

/// <summary>Cloud endpoint set (7.6: sovereign clouds swap all four, never a hardcoded host).</summary>
public sealed record ArmEndpointSet(string ManagementEndpoint, string Scope, string ApiVersion = "2022-12-01")
{
    public static ArmEndpointSet AzureCloud { get; } = new("https://management.azure.com", "https://management.azure.com/.default");
    public static ArmEndpointSet AzureUSGovernment { get; } = new("https://management.usgovcloudapi.net", "https://management.usgovcloudapi.net/.default");
    public static ArmEndpointSet AzureChinaCloud { get; } = new("https://management.chinacloudapi.cn", "https://management.chinacloudapi.cn/.default");
}
