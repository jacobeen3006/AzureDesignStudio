namespace AzureDesignStudio.Core.Cost
{
    /// <summary>One retail-price meter, as returned by the Azure Retail Prices API.</summary>
    public sealed record RetailMeter(
        string ServiceName,
        string SkuName,
        string MeterName,
        string ProductName,
        string UnitOfMeasure,
        double RetailPrice,
        string CurrencyCode);

    /// <summary>How to turn a diagram node's SKU into a monthly cost estimate.</summary>
    public sealed class CostMapEntry
    {
        public string StencilKey { get; set; } = string.Empty;
        public string ServiceLabel { get; set; } = string.Empty;
        public string ServiceName { get; set; } = string.Empty;
        /// <summary>"none" | "vm" (strip Standard_ prefix) | "appservice" (strip spaces).</summary>
        public string SkuNormalize { get; set; } = "none";
        /// <summary>Substring preferred when several meters match (e.g. "Throughput").</summary>
        public string MeterHint { get; set; } = string.Empty;
        /// <summary>"hourly" | "daily" | "usage" | "unavailable".</summary>
        public string UnitPolicy { get; set; } = "usage";
        /// <summary>Fallback SKU when the node model exposes none.</summary>
        public string DefaultSku { get; set; } = string.Empty;
        /// <summary>Product-name substrings that disqualify a meter (e.g. "Windows").</summary>
        public string[] ExcludeProductContains { get; set; } = System.Array.Empty<string>();
    }

    public enum CostStatus
    {
        Computed,
        UsageBased,
        Unavailable,
        Free,
    }

    public sealed record CostResult(
        CostStatus Status,
        double? Monthly = null,
        double? UnitPrice = null,
        string? Unit = null,
        string? MeterName = null,
        string? Sku = null,
        string? Note = null);
}
