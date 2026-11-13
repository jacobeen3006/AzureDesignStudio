using System.Text.Json;
using System.Text.Json.Serialization;
using AzureDesignStudio.Discovery.Inventory;

namespace AzureDesignStudio.Discovery.Analysis;

/// <summary>How trustworthy the retirement data is right now (plan item 6 freshness).</summary>
public enum CatalogFreshness
{
    /// <summary>Reviewed within the interval - report as current.</summary>
    Current,
    /// <summary>Past review date but within grace - show "data as of" prominently.</summary>
    ReviewDue,
    /// <summary>Long unreviewed - treat statuses as hints, not facts.</summary>
    Stale,
}

public sealed record DeprecationEntry(
    string ResourceType,
    string Status, // retired | retiring | review_due
    DateTimeOffset? RetirementDate,
    string Note,
    string? Sku = null); // set: the entry applies only to resources of that type on that SKU

/// <summary>
/// Local retirement/deprecation catalog with freshness tracking (v2 plan 7.7): maps
/// resource types to deprecation status plus a visible "data as of" date, so the report
/// never presents stale retirement data as current truth. Loaded from a host-provided
/// JSON snapshot when present; ships with a curated seed for classic resources.
/// </summary>
public sealed class DeprecationCatalog
{
    public required DateTimeOffset DataAsOf { get; init; }
    public int ReviewIntervalDays { get; init; } = 90;
    public required IReadOnlyList<DeprecationEntry> Entries { get; init; }

    public CatalogFreshness FreshnessAt(DateTimeOffset now)
    {
        var age = now - DataAsOf;
        if (age <= TimeSpan.FromDays(ReviewIntervalDays)) return CatalogFreshness.Current;
        if (age <= TimeSpan.FromDays(ReviewIntervalDays * 2)) return CatalogFreshness.ReviewDue;
        return CatalogFreshness.Stale;
    }

    /// <summary>
    /// Exact type hit first, then namespace wildcard (Microsoft.ClassicCompute/*). Only entries
    /// without a SKU match here: a SKU entry needs the resource itself (<see cref="Match(InventoryItem)"/>).
    /// </summary>
    public DeprecationEntry? Match(string resourceType) =>
        Entries.FirstOrDefault(e => e.Sku is null && string.Equals(e.ResourceType, resourceType, StringComparison.OrdinalIgnoreCase))
        ?? Entries.FirstOrDefault(e =>
            e.Sku is null &&
            e.ResourceType.EndsWith("/*", StringComparison.Ordinal) &&
            resourceType.StartsWith(e.ResourceType[..^1], StringComparison.OrdinalIgnoreCase));

    /// <summary>The entry for this resource: its SKU entry when it is on that SKU, else the type-only match.</summary>
    public DeprecationEntry? Match(InventoryItem item)
    {
        var sku = SkuOf(item);
        if (sku is not null)
        {
            var hit = Entries.FirstOrDefault(e =>
                e.Sku is not null
                && string.Equals(e.ResourceType, item.Type, StringComparison.OrdinalIgnoreCase)
                && string.Equals(e.Sku, sku, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return Match(item.Type);
    }

    // Raw.sku comes from the Resource Graph row (InventoryNormalizer): public IPs and load
    // balancers carry it as sku.name; application gateways keep the generation in sku.tier.
    private static string? SkuOf(InventoryItem item)
    {
        if (item.Raw is not { ValueKind: JsonValueKind.Object } raw
            || !raw.TryGetProperty("sku", out var sku) || sku.ValueKind != JsonValueKind.Object)
            return null;
        var key = item.Type.Equals("Microsoft.Network/applicationGateways", StringComparison.OrdinalIgnoreCase) ? "tier" : "name";
        return sku.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    public IReadOnlyList<(InventoryItem Item, DeprecationEntry Entry)> Watch(IReadOnlyList<InventoryItem> items) =>
        items.Select(i => (Item: i, Entry: Match(i)))
             .Where(x => x.Entry is not null)
             .Select(x => (x.Item, Entry: x.Entry!))
             .ToList();

    public static DeprecationCatalog Load(string? jsonPath)
    {
        if (jsonPath is not null && File.Exists(jsonPath))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<DeprecationCatalog>(File.ReadAllText(jsonPath), JsonOptions);
                if (parsed is not null) return parsed;
            }
            catch (JsonException) { /* corrupt snapshot: fall back to the built-in seed */ }
        }
        return Seed;
    }

    /// <summary>Curated seed: classic (RDFe) resources are retired; refresh via JSON snapshot.</summary>
    public static DeprecationCatalog Seed { get; } = new()
    {
        DataAsOf = new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero),
        ReviewIntervalDays = 90,
        Entries =
        [
            new("Microsoft.ClassicCompute/virtualMachines", "retired", new DateTimeOffset(2023, 11, 1, 0, 0, 0, TimeSpan.Zero), "Classic VMs retired; migrate to Azure Resource Manager VMs."),
            new("Microsoft.ClassicStorage/storageAccounts", "retired", new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero), "Classic storage retired; use Resource Manager storage accounts."),
            new("Microsoft.ClassicNetwork/virtualNetworks", "retired", new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero), "Classic virtual networks retired; use Resource Manager vnets."),
            new("Microsoft.ClassicNetwork/reservedIps", "retired", new DateTimeOffset(2024, 12, 31, 0, 0, 0, TimeSpan.Zero), "Classic reserved IPs retired; use Resource Manager public IPs."),
            new("Microsoft.ClassicCompute/*", "retired", null, "All classic compute resources retired."),
            new("Microsoft.ClassicNetwork/*", "retired", null, "All classic network resources retired."),
            new("Microsoft.ClassicStorage/*", "retired", null, "All classic storage resources retired."),
            new("Microsoft.ClassicLoadBalancer/loadBalancers", "retired", null, "Classic load balancer retired; use Resource Manager LB."),
            new("Microsoft.Network/publicIPAddresses", "retired", new DateTimeOffset(2025, 9, 30, 0, 0, 0, TimeSpan.Zero), "Basic public IPs retired; use Standard.", "Basic"),
            new("Microsoft.Network/loadBalancers", "retired", new DateTimeOffset(2025, 9, 30, 0, 0, 0, TimeSpan.Zero), "Basic load balancers retired; use Standard.", "Basic"),
            new("Microsoft.Network/applicationGateways", "retired", new DateTimeOffset(2026, 4, 28, 0, 0, 0, TimeSpan.Zero), "Application Gateway v1 (Standard) retired; use Standard_v2.", "Standard"),
            new("Microsoft.Network/applicationGateways", "retired", new DateTimeOffset(2026, 4, 28, 0, 0, 0, TimeSpan.Zero), "Application Gateway v1 (WAF) retired; use WAF_v2.", "WAF"),
            new("Microsoft.Network/frontDoors", "review_due", null, "Classic tier; confirm the retirement date on Microsoft Learn.", "Standard_Microsoft"),
            new("Microsoft.Cdn/profiles", "review_due", null, "Classic tier; confirm the retirement date on Microsoft Learn.", "Standard_Microsoft"),
            new("Microsoft.ClassicRestoreLocation/locations", "review_due", null, "Verify current retirement status before reporting."),
        ],
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}
