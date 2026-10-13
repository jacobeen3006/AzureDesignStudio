namespace AzureDesignStudio.Discovery.Inventory;

/// <summary>One facet value with its hit count (browse drawer chips, orphan-report filters).</summary>
public sealed record Facet(string Value, int Count);

/// <summary>
/// Tag/owner facets over an inventory (v2 plan 7.6): groupings computed purely from the
/// typeless rows, so facets exist for brand-new Azure services with zero mapping code.
/// Owner comes from conventional tags; untagged resources stay a visible facet (the gap
/// *is* the finding).
/// </summary>
public sealed class InventoryFacets
{
    public required IReadOnlyList<Facet> Providers { get; init; }
    public required IReadOnlyList<Facet> Locations { get; init; }
    public required IReadOnlyList<Facet> Subscriptions { get; init; }
    public required IReadOnlyList<Facet> ResourceGroups { get; init; }
    public required IReadOnlyList<Facet> Owners { get; init; }
    /// <summary>Tag key -> distinct values seen; drives the tag filter UI.</summary>
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> TagKeys { get; init; }
    public int Total { get; init; }
    public int Untagged { get; init; }
    public int CostEscalationProtected { get; init; }

    public static InventoryFacets From(IEnumerable<InventoryItem> items)
    {
        var all = items.ToList();

        IReadOnlyList<Facet> By(Func<InventoryItem, string?> key, string emptyLabel) =>
            all.GroupBy(i => key(i) is { } k && k.Length > 0 ? k : emptyLabel)
               .Select(g => new Facet(g.Key, g.Count()))
               .OrderByDescending(f => f.Count).ThenBy(f => f.Value, StringComparer.OrdinalIgnoreCase)
               .ToList();

        var tagKeys = all
            .SelectMany(i => i.Tags)
            .GroupBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<string>)g.Select(t => t.Value).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        return new InventoryFacets
        {
            Providers = By(i => i.ProviderNamespace, "(none)"),
            Locations = By(i => i.Location, "global"),
            Subscriptions = By(i => i.SubscriptionId, "(none)"),
            ResourceGroups = By(i => i.ResourceGroup, "(none)"),
            Owners = By(i => i.Owner, "(unowned)"),
            TagKeys = tagKeys,
            Total = all.Count,
            Untagged = all.Count(i => i.Tags.Count == 0),
            CostEscalationProtected = all.Count(i => i.HasCostEscalationSignal),
        };
    }
}
