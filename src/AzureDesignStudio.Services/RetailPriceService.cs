using AzureDesignStudio.SharedModels.Protos;
using Grpc.Core;
using Microsoft.Extensions.Caching.Memory;
using System.Text;
using System.Text.Json;

namespace AzureDesignStudio.Services
{
    /// <summary>
    /// Proxy for the Azure Retail Prices API (prices.azure.com, public OData, no auth).
    /// Server-side so the WASM client avoids CORS; responses cached per query.
    /// Anonymous by design: retail prices are public data with no user context.
    /// </summary>
    public class RetailPriceService : Cost.CostBase
    {
        private const int MaxPages = 10;
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(1);

        private readonly IHttpClientFactory httpClientFactory;
        private readonly IMemoryCache cache;
        private readonly ILogger<RetailPriceService> logger;

        public RetailPriceService(IHttpClientFactory httpClientFactory, IMemoryCache cache, ILogger<RetailPriceService> logger)
        {
            this.httpClientFactory = httpClientFactory;
            this.cache = cache;
            this.logger = logger;
        }

        public override async Task<RetailPriceResponse> GetRetailPrices(RetailPriceRequest request, ServerCallContext context)
        {
            var response = new RetailPriceResponse();
            if (string.IsNullOrWhiteSpace(request.ServiceName) || string.IsNullOrWhiteSpace(request.ArmRegionName)
                || !IsPlausibleFilterValue(request.ServiceName, required: true)
                || !IsPlausibleFilterValue(request.ArmRegionName, required: true)
                || !IsPlausibleFilterValue(request.SkuName, required: false)
                || !IsPlausibleFilterValue(request.MeterName, required: false)
                || !IsPlausibleCurrency(request.CurrencyCode))
            {
                response.StatusCode = StatusCodes.Status400BadRequest;
                return response;
            }

            var url = BuildRequestUrl(request, includeRegion: true);
            try
            {
                var items = await GetCachedAsync(url, context.CancellationToken);
                if (items is { Count: 0 } && !string.IsNullOrWhiteSpace(request.ArmRegionName))
                {
                    // Regionless services (e.g. Load Balancer) price against Global/edge zones.
                    url = BuildRequestUrl(request, includeRegion: false);
                    items = await GetCachedAsync(url, context.CancellationToken);
                }
                if (items is not null)
                    response.Prices.AddRange(items);
                response.StatusCode = StatusCodes.Status200OK;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Retail prices query failed for {Url}", url);
                response.StatusCode = StatusCodes.Status502BadGateway;
            }
            return response;
        }

        // This endpoint is anonymous and its cache key is built from the caller's own text, so
        // both are bounded: a caller sending endless distinct queries otherwise grew the cache
        // without limit and fanned out up to MaxPages upstream requests for each one.
        private const int MaxFilterValueLength = 120;
        private const int MaxCachedQueries = 2000;
        private static int cachedQueries;

        internal static bool IsPlausibleFilterValue(string? value, bool required)
        {
            if (string.IsNullOrWhiteSpace(value))
                return !required;
            if (value.Length > MaxFilterValueLength)
                return false;
            foreach (var c in value)
                if (char.IsControl(c))
                    return false;
            return true;
        }

        internal static bool IsPlausibleCurrency(string? code) =>
            string.IsNullOrWhiteSpace(code) || (code.Trim().Length == 3 && code.Trim().All(char.IsAsciiLetter));

        private async Task<List<RetailPriceItem>> GetCachedAsync(string url, CancellationToken cancellationToken)
        {
            if (cache.TryGetValue(url, out List<RetailPriceItem>? cached) && cached is not null)
                return cached;

            var (items, complete) = await FetchPagesAsync(url, cancellationToken);

            // A list cut off at the page limit is returned but not remembered: cached, it would
            // answer as if it were the whole price list for the next hour.
            if (complete && Interlocked.Increment(ref cachedQueries) <= MaxCachedQueries)
            {
                using var entry = cache.CreateEntry(url);
                entry.AbsoluteExpirationRelativeToNow = CacheLifetime;
                entry.Value = items;
                entry.RegisterPostEvictionCallback(static (_, _, _, _) => Interlocked.Decrement(ref cachedQueries));
            }
            else if (complete)
            {
                Interlocked.Decrement(ref cachedQueries);
            }

            return items;
        }

        internal static string BuildRequestUrl(RetailPriceRequest request, bool includeRegion = true)
        {
            var filter = new StringBuilder();
            filter.Append($"serviceName eq '{ODataEscape(request.ServiceName)}'");
            if (includeRegion)
                filter.Append($" and armRegionName eq '{ODataEscape(request.ArmRegionName)}'");
            filter.Append(" and priceType eq 'Consumption'");
            if (!string.IsNullOrWhiteSpace(request.SkuName))
                filter.Append($" and skuName eq '{ODataEscape(request.SkuName)}'");
            if (!string.IsNullOrWhiteSpace(request.MeterName))
                filter.Append($" and meterName eq '{ODataEscape(request.MeterName)}'");

            var url = new StringBuilder("https://prices.azure.com/api/retail/prices?$filter=");
            url.Append(Uri.EscapeDataString(filter.ToString()));
            if (!string.IsNullOrWhiteSpace(request.CurrencyCode))
                url.Append($"&currencyCode={Uri.EscapeDataString(request.CurrencyCode.Trim().ToUpperInvariant())}");
            return url.ToString();
        }

        private async Task<(List<RetailPriceItem> Items, bool Complete)> FetchPagesAsync(string url, CancellationToken cancellationToken)
        {
            var client = httpClientFactory.CreateClient("RetailPrices");
            var items = new List<RetailPriceItem>();
            string? pageUrl = url;
            for (var page = 0; page < MaxPages && pageUrl is not null; page++)
            {
                // Follow paging only on the prices host the query started on.
                if (page > 0 && !pageUrl.StartsWith("https://prices.azure.com/", StringComparison.OrdinalIgnoreCase))
                    return (items, false);

                using var document = JsonDocument.Parse(await client.GetStringAsync(pageUrl, cancellationToken));
                var root = document.RootElement;
                if (root.TryGetProperty("Items", out var list) && list.ValueKind == JsonValueKind.Array)
                    foreach (var item in list.EnumerateArray())
                        items.Add(MapItem(item));
                pageUrl = root.TryGetProperty("NextPageLink", out var next) && next.ValueKind == JsonValueKind.String
                    ? next.GetString()
                    : null;
            }
            return (items, pageUrl is null);
        }

        private static RetailPriceItem MapItem(JsonElement item) => new()
        {
            ServiceName = GetString(item, "serviceName"),
            SkuName = GetString(item, "skuName"),
            MeterName = GetString(item, "meterName"),
            ProductName = GetString(item, "productName"),
            UnitOfMeasure = GetString(item, "unitOfMeasure"),
            RetailPrice = item.TryGetProperty("retailPrice", out var price) && price.TryGetDouble(out var value) ? value : 0,
            CurrencyCode = GetString(item, "currencyCode"),
            ArmRegionName = GetString(item, "armRegionName"),
        };

        private static string GetString(JsonElement item, string name) =>
            item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : string.Empty;

        private static string ODataEscape(string value) => value.Replace("'", "''");
    }
}
