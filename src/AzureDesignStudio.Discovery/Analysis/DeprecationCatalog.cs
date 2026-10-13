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
    string Note);

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

    /// <summary>Exact type hit first, then namespace wildcard (Microsoft.ClassicCompute/*).</summary>
    public DeprecationEntry? Match(string resourceType) =>
        Entries.FirstOrDefault(e => string.Equals(e.ResourceType, resourceType, StringComparison.OrdinalIgnoreCase))
        ?? Entries.FirstOrDefault(e =>
            e.ResourceType.EndsWith("/*", StringComparison.Ordinal) &&
            resourceType.StartsWith(e.ResourceType[..^1], StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<(InventoryItem Item, DeprecationEntry Entry)> Watch(IReadOnlyList<InventoryItem> items) =>
        items.Select(i => (Item: i, Entry: Match(i.Type)))
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
        DataAsOf = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero),
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
            new("Microsoft.ClassicRestoreLocation/locations", "review_due", null, "Verify current retirement status before reporting."),
        ],
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}
