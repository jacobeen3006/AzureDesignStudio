using System.Text.Json;
using AzureDesignStudio.Core.Cost;

namespace AzureDesignStudio.Services
{
    public record CostNodeInput(string Name, string StencilKey, string? Sku);

    public record CostLine(string NodeName, string ServiceLabel, CostResult Result);

    public record CostBreakdownLine(string ServiceLabel, double Monthly);

    public record CostEstimateResult(
        IReadOnlyList<CostLine> Lines,
        double? TotalMonthly,
        string Currency,
        bool UsedFallback,
        IReadOnlyList<CostBreakdownLine> Breakdown);

    /// <summary>
    /// Turns diagram nodes into monthly cost estimates: cost map (wwwroot/cost-map.json)
    /// decides the query per node type, the server proxy supplies meters, and
    /// wwwroot/price-snapshot.json is the offline fallback when the proxy is unreachable.
    /// </summary>
    public class CostService
    {
        private readonly CostGrpcService _costGrpc;
        private readonly IHttpClientFactory _clientFactory;
        private HttpClient? _http;
        // WebView hosts initialize NavigationManager after the first render, so the
        // client (and its base address) is resolved on first request, not in the ctor.
        private HttpClient Http => _http ??= _clientFactory.CreateClient("AzureDesignStudio.Root");
        private List<CostMapEntry>? _costMap;
        private List<RetailMeter>? _snapshot;

        public CostService(CostGrpcService costGrpc, IHttpClientFactory clientFactory)
        {
            _costGrpc = costGrpc;
            _clientFactory = clientFactory;
        }

        public async Task<CostEstimateResult> EstimateAsync(IReadOnlyList<CostNodeInput> nodes, string region, string currency)
        {
            _costMap ??= JsonSerializer.Deserialize<List<CostMapEntry>>(
                await Http.GetStringAsync("cost-map.json"),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();

            var usedFallback = false;
            var lines = new List<CostLine>();

            foreach (var node in nodes)
            {
                var entry = _costMap.FirstOrDefault(e => e.StencilKey == node.StencilKey);
                if (entry is null)
                {
                    lines.Add(new CostLine(node.Name, node.StencilKey,
                        new CostResult(CostStatus.Unavailable, Note: "no pricing entry for this resource type")));
                    continue;
                }

                var result = await EstimateNodeAsync(entry, node.Sku, region, currency);
                if (result.UsedFallback) usedFallback = true;
                lines.Add(new CostLine(node.Name, entry.ServiceLabel, result.Result));
            }

            var computed = lines.Where(l => l.Result.Status == CostStatus.Computed && l.Result.Monthly is not null).ToList();
            double? total = computed.Count > 0 ? Math.Round(computed.Sum(l => l.Result.Monthly!.Value), 2) : null;
            var breakdown = computed
                .GroupBy(l => l.ServiceLabel)
                .Select(g => new CostBreakdownLine(g.Key, Math.Round(g.Sum(l => l.Result.Monthly!.Value), 2)))
                .OrderByDescending(b => b.Monthly)
                .ToList();

            return new CostEstimateResult(lines, total, currency, usedFallback, breakdown);
        }

        private async Task<(CostResult Result, bool UsedFallback)> EstimateNodeAsync(CostMapEntry entry, string? nodeSku, string region, string currency)
        {
            if (string.IsNullOrEmpty(entry.ServiceName))
                return (CostEstimator.Estimate(entry, nodeSku, Array.Empty<RetailMeter>()), false);

            var sku = string.IsNullOrWhiteSpace(nodeSku) ? entry.DefaultSku : nodeSku!;
            var querySku = CostEstimator.NormalizeSku(sku, entry.SkuNormalize).Replace('_', ' ');

            try
            {
                var meters = await _costGrpc.GetRetailPrices(entry.ServiceName, region, querySku, currency);
                if (meters.Count == 0 && querySku.Length > 0)
                    meters = await _costGrpc.GetRetailPrices(entry.ServiceName, region, string.Empty, currency);
                return (CostEstimator.Estimate(entry, nodeSku, meters), false);
            }
            catch (Exception)
            {
                _snapshot ??= JsonSerializer.Deserialize<List<RetailMeter>>(
                    await Http.GetStringAsync("price-snapshot.json"),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
                var meters = _snapshot.Where(m => m.ServiceName == entry.ServiceName).ToList();
                return (CostEstimator.Estimate(entry, nodeSku, meters), true);
            }
        }
    }
}
