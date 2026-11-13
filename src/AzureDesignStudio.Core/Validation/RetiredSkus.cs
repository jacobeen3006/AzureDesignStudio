// Licensed under the MIT License. See LICENSE in the project root for license information.
namespace AzureDesignStudio.Core.Validation
{
    /// <summary>A SKU Azure no longer deploys, the date it was retired and what to use instead.</summary>
    public sealed record RetiredSku(string ResourceType, string Sku, DateOnly RetiredOn, string Replacement);

    /// <summary>Network SKUs Azure has retired. A design that still holds one cannot be deployed.</summary>
    public static class RetiredSkus
    {
        public static IReadOnlyList<RetiredSku> All { get; } =
        [
            new("Microsoft.Network/publicIPAddresses", "Basic", new DateOnly(2025, 9, 30), "Standard"),
            new("Microsoft.Network/loadBalancers", "Basic", new DateOnly(2025, 9, 30), "Standard"),
            new("Microsoft.Network/applicationGateways", "Standard", new DateOnly(2026, 4, 28), "Standard_v2"),
            new("Microsoft.Network/applicationGateways", "WAF", new DateOnly(2026, 4, 28), "WAF_v2"),
        ];

        /// <summary>The retired entry for this resource type and SKU (case-insensitive), or null.</summary>
        public static RetiredSku? Match(string resourceType, string? sku) =>
            string.IsNullOrWhiteSpace(sku)
                ? null
                : All.FirstOrDefault(r =>
                    string.Equals(r.ResourceType, resourceType, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(r.Sku, sku.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
