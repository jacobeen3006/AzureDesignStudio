using System.Globalization;

namespace AzureDesignStudio.Core.Cost
{
    /// <summary>
    /// Pure meter-selection and monthly-cost math. No I/O: the caller supplies
    /// meters (live proxy or offline snapshot) so this stays unit-testable.
    /// </summary>
    public static class CostEstimator
    {
        public const double HoursPerMonth = 730;      // Azure's billing convention
        public const double DaysPerMonth = 30.44;     // 730 / 24

        /// <summary>Node SKU -> API SKU. "vm": Standard_D2s_v3 -> D2s_v3. "storage": Standard_LRS -> Hot LRS.</summary>
        public static string NormalizeSku(string sku, string mode) => mode switch
        {
            "vm" when sku.StartsWith("Standard_", System.StringComparison.OrdinalIgnoreCase) => sku[9..],
            "storage" when sku.StartsWith("Standard_", System.StringComparison.OrdinalIgnoreCase) => "Hot " + sku[9..],
            _ => sku,
        };

        private static string CompareKey(string sku) => sku.Replace(" ", "").Replace("_", "").ToLowerInvariant();
        /// <summary>
        /// Select the meter for a node SKU and compute the monthly estimate.
        /// Never guesses: no matching meter -> Unavailable, non-time unit -> UsageBased.
        /// </summary>
        public static CostResult Estimate(CostMapEntry entry, string? nodeSku, IReadOnlyList<RetailMeter> meters)
        {
            if (entry.UnitPolicy == "unavailable")
                return new CostResult(CostStatus.Unavailable, Note: "usage-based pricing");
            if (entry.UnitPolicy == "free")
                return new CostResult(CostStatus.Free, Note: string.IsNullOrEmpty(entry.MeterHint) ? "no extra charge" : entry.MeterHint);

            var sku = string.IsNullOrWhiteSpace(nodeSku) ? entry.DefaultSku : nodeSku!;
            if (string.IsNullOrWhiteSpace(sku))
                return new CostResult(CostStatus.Unavailable, Note: "no SKU on node");

            if (entry.UnitPolicy == "unavailable")
                return new CostResult(CostStatus.Unavailable, Note: "usage-based pricing");

            if (entry.UnitPolicy == "free")
                return new CostResult(CostStatus.Free, Note: string.IsNullOrEmpty(entry.MeterHint) ? "no extra charge" : entry.MeterHint);
            sku = NormalizeSku(sku, entry.SkuNormalize);
            var key = CompareKey(sku);

            var candidates = meters.Where(m =>
                CompareKey(m.SkuName) == key &&
                !entry.ExcludeProductContains.Any(x => m.ProductName.Contains(x, System.StringComparison.OrdinalIgnoreCase)));

            if (entry.UnitPolicy == "usage")
            {
                var usage = Pick(candidates, entry.MeterHint, unit: null);
                return usage is null
                    ? new CostResult(CostStatus.Unavailable, Sku: sku, Note: "no meter found")
                    : new CostResult(CostStatus.UsageBased, UnitPrice: usage.RetailPrice, Unit: usage.UnitOfMeasure, MeterName: usage.MeterName, Sku: usage.SkuName);
            }

            var unit = entry.UnitPolicy == "hourly" ? "1 Hour" : "1/Day";
            var meter = Pick(candidates.Where(m => m.UnitOfMeasure == unit), entry.MeterHint, unit);
            if (meter is null)
                return new CostResult(CostStatus.Unavailable, Sku: sku, Note: $"no {unit} meter found");

            var monthly = meter.RetailPrice * (entry.UnitPolicy == "hourly" ? HoursPerMonth : DaysPerMonth);
            return new CostResult(CostStatus.Computed, Monthly: System.Math.Round(monthly, 2), MeterName: meter.MeterName, Sku: meter.SkuName);
        }

        /// <summary>Hint-matching meter wins; otherwise the most expensive match (conservative upper bound).</summary>
        private static RetailMeter? Pick(IEnumerable<RetailMeter> candidates, string hint, string? unit)
        {
            var list = candidates.ToList();
            if (list.Count == 0) return null;
            if (!string.IsNullOrEmpty(hint))
            {
                var hinted = list.Where(m => m.MeterName.Contains(hint, System.StringComparison.OrdinalIgnoreCase)).ToList();
                if (hinted.Count > 0) list = hinted;
            }
            return list.OrderByDescending(m => m.RetailPrice).First();
        }

        public static string FormatMoney(double? value, string currency) =>
            value is null ? "-" : value.Value.ToString(currency == "USD" ? "$#,##0.00" : "#,##0.00 " + currency, CultureInfo.InvariantCulture);
    }
}
