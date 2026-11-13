using System.Globalization;
using System.Text.Json;
using AzureDesignStudio.Discovery.Arm;

namespace AzureDesignStudio.Discovery.Cost;

/// <summary>
/// Actual spend from the Cost Management query API (v2 plan 9.5), any ARM scope
/// (subscription, RG, MG) - Reader-on-scope is not enough here, the probe declares
/// `Cost Management Reader`. Long windows run as async jobs; the transport polls them.
/// </summary>
public sealed class ActualSpendClient(ArmTransport transport)
{
    /// <summary>Queries one window over one scope, grouped by every dimension worth cutting by.</summary>
    public async Task<ActualSpendResult> QueryAsync(
        string scope, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var ep = transport.Endpoints.ManagementEndpoint;
        var url = $"{ep}/{scope.TrimStart('/')}/providers/Microsoft.CostManagement/query?api-version=2023-08-01";
        var body = BuildUsageRequest(from, to);
        var merged = MergePages(await ReadAllPagesAsync(url, body, ct).ConfigureAwait(false));

        using var doc = JsonDocument.Parse(merged);
        return CostQueryParser.ToResult(doc.RootElement, scope, new Uri(ep).Host,
            from, to, DateTimeOffset.UtcNow);
    }

    /// <summary>A grouped query over a busy scope does not fit one response: the service
    /// returns <c>properties.nextLink</c> and expects the same body POSTed to it. Reading only
    /// the first page understated every total with no sign that anything was missing.</summary>
    private async Task<List<string>> ReadAllPagesAsync(string url, string body, CancellationToken ct)
    {
        var pages = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? next = url;
        while (next is not null)
        {
            if (!visited.Add(next))
                throw new ResourceGraph.ResourceGraphException("PagingLoop", "Cost Management returned the same nextLink twice.");
            if (pages.Count >= MaxPages)
                throw new ResourceGraph.ResourceGraphException("PagingLimit", $"Cost Management returned more than {MaxPages} pages for one query.");
            if (pages.Count > 0 && !SameHost(url, next))
                throw new ResourceGraph.ResourceGraphException("PagingHostChanged", "Cost Management paging pointed at another host; not followed.");

            var page = await transport.PostAsyncPolled(next, body, ct: ct).ConfigureAwait(false);
            pages.Add(page);
            next = NextLinkOf(page);
        }

        return pages;
    }

    private const int MaxPages = 200;

    private static bool SameHost(string first, string next) =>
        Uri.TryCreate(first, UriKind.Absolute, out var a) && Uri.TryCreate(next, UriKind.Absolute, out var b)
        && string.Equals(a.Host, b.Host, StringComparison.OrdinalIgnoreCase)
        && string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase);

    internal static string? NextLinkOf(string pageJson)
    {
        using var doc = JsonDocument.Parse(pageJson);
        return doc.RootElement.ValueKind == JsonValueKind.Object
            && doc.RootElement.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object
            && props.TryGetProperty("nextLink", out var link) && link.ValueKind == JsonValueKind.String
            && link.GetString() is { Length: > 0 } next
            ? next
            : null;
    }

    /// <summary>One response envelope holding the first page's columns and every page's rows.</summary>
    internal static string MergePages(IReadOnlyList<string> pages)
    {
        if (pages.Count == 1)
            return pages[0];

        var first = System.Text.Json.Nodes.JsonNode.Parse(pages[0]) as System.Text.Json.Nodes.JsonObject
            ?? throw new JsonException("Cost Management response is not a JSON object.");
        if (first["properties"] is not System.Text.Json.Nodes.JsonObject properties
            || properties["rows"] is not System.Text.Json.Nodes.JsonArray rows)
            return pages[0];

        foreach (var page in pages.Skip(1))
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(page)?["properties"]?["rows"] is System.Text.Json.Nodes.JsonArray more)
                foreach (var row in more)
                    rows.Add(row?.DeepClone());
        }

        properties.Remove("nextLink");
        return first.ToJsonString();
    }

    /// <summary>Usage query body: custom window, summed cost, grouped by the 9.5 dimension set.</summary>
    public static string BuildUsageRequest(DateTimeOffset from, DateTimeOffset to)
    {
        const string Template = """
        {"type":"Usage","timeframe":"Custom","timePeriod":{"from":"__FROM__","to":"__TO__"},
        "dataset":{"granularity":"None",
        "aggregation":{"TotalCost":{"name":"Cost","function":"SUM"}},
        "aggregationTotality":"Full",
        "grouping":[{"type":"Dimension","name":"ResourceId"},
        {"type":"Dimension","name":"ResourceGroupName"},
        {"type":"Dimension","name":"ServiceFamily"},
        {"type":"Dimension","name":"MeterCategory"},
        {"type":"Dimension","name":"ResourceLocation"}]}}
        """;

        return Template
            .Replace("__FROM__", Stamp(from))
            .Replace("__TO__", Stamp(to));
    }

    // API accepts datetime with seconds precision; drop sub-second noise (stable, cache-friendly bodies).
    private static string Stamp(DateTimeOffset t) =>
        t.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
