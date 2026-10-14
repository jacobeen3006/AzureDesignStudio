using System.Text.Json;
using AzureDesignStudio.Discovery.Analysis;
using AzureDesignStudio.Discovery.Cost;
using AzureDesignStudio.Discovery.Inventory;

namespace AzureDesignStudio.Discovery.Decommission;

/// <summary>One retirement candidate [67]: its measured spend, the reference-graph idleness
/// signals, and whether it ranks as an idle-cost case.</summary>
/// <param name="Cost">Spend attributed to this resource over the provenance window (the caller
/// picks a 30/90-day window; the figure is the window total, never extrapolated here).</param>
public sealed record IdleCostRow(
    string ResourceId,
    string Name,
    string Type,
    decimal? Cost,
    string Currency,
    IReadOnlyList<string> IdleReasons,
    bool Idle);

public sealed record IdleCostReport(
    IReadOnlyList<IdleCostRow> Rows,
    SpendProvenance? Provenance,
    decimal TotalCost,
    IReadOnlyList<string> Caveats);

/// <summary>[67] Rank retire candidates by spend and idleness so the savings case is quantified
/// per candidate. The hard part is honesty about which "idle" is meant: only ARM-reference
/// unattached-ness is provable from a scan - traffic/request idleness is an Azure Monitor metric
/// question and is always stated as a caveat.</summary>
public static class IdleCostRanker
{
    private static readonly string[] IdleKinds =
    [
        "microsoft.compute/disks",
        "microsoft.network/publicipaddresses",
        "microsoft.network/networkinterfaces",
    ];

    public static IdleCostReport Rank(IReadOnlyList<InventoryItem> inventory, ActualSpendResult? spend)
    {
        var caveats = new List<string>();
        if (spend is null)
            caveats.Add("spend-not-supplied: no cost figures; ranking carries $ per row as unknown and orders by idle signals only");
        caveats.Add("metrics-not-supplied: 30/90-day traffic/request idleness needs Azure Monitor; only ARM-reference 'unattached' is judged here");

        var cost = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        if (spend is not null)
        {
            foreach (var row in spend.Rows)
            {
                if (row.ResourceId is not { } rid) continue;
                var key = rid.Trim();
                cost[key] = cost.TryGetValue(key, out var c) ? c + row.Cost : row.Cost;
            }

            var scanned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in inventory) scanned.Add(item.Id.Trim());
            var unmatched = spend.Rows.Count(r => r.ResourceId is { } id && !scanned.Contains(id.Trim()));
            if (unmatched > 0)
                caveats.Add($"spend-rows-unmatched:{unmatched}: some $ are attributed to ids outside this inventory slice");
        }

        var graph = TopologyDeriver.Derive(inventory);
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var diskLeases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var edge in graph.Edges)
        {
            referenced.Add(edge.To.Trim());
            if (edge.Kind == TopologyEdgeKind.AttachedTo) diskLeases.Add(edge.To.Trim());
        }

        var rows = new List<IdleCostRow>();
        foreach (var item in inventory)
        {
            var type = item.Type.ToLowerInvariant();
            if (!IdleKinds.Contains(type)) continue;
            var id = item.Id.Trim();

            List<string> reasons = [];
            if (type == "microsoft.compute/disks")
            {
                if (ManagedBy(item) is null && !diskLeases.Contains(id))
                    reasons.Add("unattached-disk: no VM lease holds it");
            }
            else if (type == "microsoft.network/publicipaddresses")
            {
                if (!referenced.Contains(id))
                    reasons.Add("unattached-public-ip: no NIC configuration or load-balancer frontend references it");
            }
            else
            {
                if (!referenced.Contains(id))
                    reasons.Add("orphan-nic: no VM network profile references it");
            }

            rows.Add(new IdleCostRow(item.Id, item.Name, item.Type,
                cost.TryGetValue(id, out var c) ? c : null,
                spend?.Provenance.Currency ?? "", reasons, reasons.Count > 0));
        }

        rows.Sort((a, b) =>
        {
            var byCost = (b.Cost ?? -1m).CompareTo(a.Cost ?? -1m);
            if (byCost != 0) return byCost;
            var byIdle = b.Idle.CompareTo(a.Idle);
            return byIdle != 0 ? byIdle
                : string.CompareOrdinal(a.ResourceId.ToLowerInvariant(), b.ResourceId.ToLowerInvariant());
        });

        var total = 0m;
        foreach (var r in rows) total += r.Cost ?? 0m;
        return new IdleCostReport(rows, spend?.Provenance, total, caveats);
    }

    private static string? ManagedBy(InventoryItem item) =>
        item.Raw is { } p && p.ValueKind == JsonValueKind.Object &&
        p.TryGetProperty("managedBy", out var mb) && mb.ValueKind == JsonValueKind.String
            ? mb.GetString()
            : null;
}
