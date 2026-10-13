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
        string? next = url;
        while (next is not null)
        {
            using var doc = JsonDocument.Parse(await SendAsync(HttpMethod.Get, next, body: null, ct).ConfigureAwait(false));
            if (doc.RootElement.TryGetProperty(valueProperty, out var arr) && arr.ValueKind == JsonValueKind.Array)
                foreach (var item in arr.EnumerateArray()) items.Add(item.Clone());

            next = doc.RootElement.TryGetProperty("nextLink", out var link) && link.ValueKind == JsonValueKind.String
                ? link.GetString()
                : null;
        }
        return items;
    }

    public Task<string> GetAsync(string url, CancellationToken ct = default) => SendAsync(HttpMethod.Get, url, null, ct);
    public Task<string> PostAsync(string url, string json, CancellationToken ct = default) => SendAsync(HttpMethod.Post, url, json, ct);

    private async Task<string> SendAsync(HttpMethod method, string url, string? body, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                using var message = new HttpRequestMessage(method, url);
                message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetToken());
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

                var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                    throw ResourceGraphException.FromErrorBody((int)response.StatusCode, text);
                return text;
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private string GetToken() => credential
        .GetTokenAsync(new TokenRequestContext(new[] { Endpoints.Scope }), CancellationToken.None)
        .GetAwaiter().GetResult().Token;
}

/// <summary>Cloud endpoint set (7.6: sovereign clouds swap all four, never a hardcoded host).</summary>
public sealed record ArmEndpointSet(string ManagementEndpoint, string Scope, string ApiVersion = "2022-12-01")
{
    public static ArmEndpointSet AzureCloud { get; } = new("https://management.azure.com", "https://management.azure.com/.default");
    public static ArmEndpointSet AzureUSGovernment { get; } = new("https://management.usgovcloudapi.net", "https://management.usgovcloudapi.net/.default");
    public static ArmEndpointSet AzureChinaCloud { get; } = new("https://management.chinacloudapi.cn", "https://management.chinacloudapi.cn/.default");
}
