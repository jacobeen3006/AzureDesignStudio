using System.Text.Json;
using AzureDesignStudio.Discovery.Inventory;

namespace AzureDesignStudio.Discovery.Drift;

public enum ResourceChangeKind { Added, Removed, Changed }

/// <summary>One leaf of the property tree that differs (path like <c>properties.sku.name</c>, arrays as <c>[i]</c>).</summary>
public sealed record PropertyDiff(string Path, string? PreviousJson, string? NewJson);

/// <summary>What happened to one resource between two typeless inventories.</summary>
public sealed record ResourceDrift(
    string ResourceId,
    string ResourceType,
    ResourceChangeKind Kind,
    IReadOnlyList<PropertyDiff> Properties);

/// <summary>
/// Drift between two point-in-time inventories (v2 plan 9.2): scan↔scan, design↔design
/// or design↔live - every side arrives through the same typeless
/// <see cref="InventoryItem"/> shape, so comparing needs no per-service code.
/// </summary>
public sealed class ScanDiffer
{
    private readonly Func<string, bool> _isVolatile;

    /// <summary>
    /// <paramref name="isVolatile"/> paths (exact JSON path) are reported but excluded
    /// from the <see cref="ScanDiff.Changed"/> count - platform churn the operator was
    /// not asked about (provisioningState, effective rules timestamps, ...).
    /// </summary>
    public ScanDiffer(Func<string, bool>? isVolatilePath = null) =>
        _isVolatile = isVolatilePath ?? DefaultVolatile;

    public static readonly string[] DefaultVolatilePaths =
    [
        "properties.provisioningState",
    ];

    private static bool DefaultVolatile(string path) => Array.IndexOf(DefaultVolatilePaths, path) >= 0;

    /// <summary>Per-resource property-diff cap; a mis-versioned dump can change thousands of leaves.</summary>
    public const int MaxPropertyDiffs = 200;

    public ScanDiff Diff(IReadOnlyList<InventoryItem> before, IReadOnlyList<InventoryItem> after)
    {
        var oldItems = Index(before);
        var newItems = Index(after);

        var added = new List<ResourceDrift>();
        var changed = new List<ResourceDrift>();
        var removed = new List<ResourceDrift>();
        var changedResources = 0;

        foreach (var (id, old) in oldItems)
        {
            if (!newItems.TryGetValue(id, out var @new))
            {
                removed.Add(new ResourceDrift(old.Id, old.Type, ResourceChangeKind.Removed, []));
                continue;
            }

            var props = DiffProperties(old, @new);
            if (props.Count == 0) continue;
            if (props.Any(p => !_isVolatile(p.Path))) changedResources++;
            changed.Add(new ResourceDrift(@new.Id, @new.Type, ResourceChangeKind.Changed, props));
        }

        foreach (var (id, @new) in newItems)
            if (!oldItems.ContainsKey(id))
                added.Add(new ResourceDrift(@new.Id, @new.Type, ResourceChangeKind.Added, []));

        var drift = added
            .Concat(changed)
            .Concat(removed)
            .OrderBy(d => d.ResourceId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ScanDiff(drift, InventoryNormalizer.ContentHash(before), InventoryNormalizer.ContentHash(after), changedResources);
    }

    private static Dictionary<string, InventoryItem> Index(IReadOnlyList<InventoryItem> items)
    {
        var map = new Dictionary<string, InventoryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
            map[item.Id] = item;
        return map;
    }

    private List<PropertyDiff> DiffProperties(InventoryItem old, InventoryItem @new)
    {
        var beforeFlat = Flatten(old);
        var afterFlat = Flatten(@new);
        var diffs = new List<PropertyDiff>(MaxPropertyDiffs);

        foreach (var (path, value) in beforeFlat)
        {
            if (diffs.Count >= MaxPropertyDiffs) break;
            if (!afterFlat.TryGetValue(path, out var now))
                diffs.Add(new PropertyDiff(path, value, null));
            else if (now != value)
                diffs.Add(new PropertyDiff(path, value, now));
        }

        foreach (var (path, value) in afterFlat)
        {
            if (diffs.Count >= MaxPropertyDiffs) break;
            if (!beforeFlat.ContainsKey(path))
                diffs.Add(new PropertyDiff(path, null, value));
        }

        return diffs;
    }

    /// <summary>Stable, sorted leaf map (path → raw JSON), incl. location + tags drift.</summary>
    internal static SortedDictionary<string, string> Flatten(InventoryItem item)
    {
        var leaves = new SortedDictionary<string, string>(StringComparer.Ordinal);
        leaves["location"] = JsonSerializer.Serialize(item.Location);
        foreach (var (k, v) in item.Tags.OrderBy(t => t.Key, StringComparer.Ordinal))
            leaves[$"tags.{k}"] = JsonSerializer.Serialize(v);
        if (item.Raw is { } raw)
            Walk(raw, "properties", leaves);
        return leaves;
    }

    private static void Walk(JsonElement value, string path, SortedDictionary<string, string> leaves)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in value.EnumerateObject())
                    Walk(prop.Value, $"{path}.{prop.Name}", leaves);
                break;
            case JsonValueKind.Array:
                var i = 0;
                foreach (var el in value.EnumerateArray())
                    Walk(el, $"{path}[{i++}]", leaves);
                break;
            default:
                leaves[path] = value.GetRawText();
                break;
        }
    }
}

/// <summary>
/// Diff result, ordering Added → Changed → Removed with resource ids sorted inside.
/// <see cref="ChangedResources"/> is counted by the differ (its volatile-path rule is
/// instance state), so a resource whose only diffs are platform churn never counts.
/// </summary>
public sealed record ScanDiff(
    IReadOnlyList<ResourceDrift> Drift,
    string BeforeHash,
    string AfterHash,
    int ChangedResources)
{
    public int Added => Drift.Count(d => d.Kind == ResourceChangeKind.Added);
    public int Removed => Drift.Count(d => d.Kind == ResourceChangeKind.Removed);
    public bool IsEmpty => Added == 0 && Removed == 0 && ChangedResources == 0;
}
