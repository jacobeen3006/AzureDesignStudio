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
        var json = await transport
            .PostAsyncPolled(url, BuildUsageRequest(from, to), ct: ct)
            .ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        return CostQueryParser.ToResult(doc.RootElement, scope, new Uri(ep).Host,
            from, to, DateTimeOffset.UtcNow);
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
