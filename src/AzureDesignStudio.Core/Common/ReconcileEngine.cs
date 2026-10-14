using System;
using System.Collections.Generic;
using System.Linq;
using AzureDesignStudio.Core.DTO;

namespace AzureDesignStudio.Core.Common;

/// <summary>Where one live resource stands against the open design (v2 plan [58] reconcile).</summary>
public enum DesignState
{
    /// <summary>Live in Azure, absent from the design - the adopt verb.</summary>
    InLiveNotDesign,
    /// <summary>In both planes; the canvas carries no editable intent ARM would push, so the pair is consistent.</summary>
    InBothSynced,
    /// <summary>In both planes but the design still says an old name/location - informational; ARM export rebuilds from canvas names, never sends it.</summary>
    Drifted,
}

/// <summary>One reconcilable row: a live resource classified against the design.</summary>
public sealed record ReconcileRow(string Id, string Type, string Name, string? Location, DesignState State, string? DesignName);

/// <summary>Full per-resource verdict plus the design-only side that has no live row.</summary>
/// <param name="Rows">Live resources with their state, ordinal by trimmed id.</param>
/// <param name="DesignOnly">Design nodes carrying an ARM id but absent from Azure - deploy candidates (via what-if/apply) or detach candidates; never deleted here.</param>
/// <param name="UntrackedDesignNodes">Design nodes with no ARM id (hand-made or placeholder) - no live counterpart to reconcile against.</param>
public sealed record ReconciliationReport(
    IReadOnlyList<ReconcileRow> Rows,
    IReadOnlyList<string> DesignOnly,
    int UntrackedDesignNodes)
{
    public int CountIn(DesignState state) => Rows.Count(r => r.State == state);
}

/// <summary>
/// Per-resource reconcile (v2 plan [58]): classify every live resource against the open
/// design so the UI can offer adopt / detach verbs honestly. Identity is the durable ArmId
/// (legacy designs: the ARM id in Id) stamped by adoption/refresh (same key as <see cref="LivePlaneMerger"/>).
/// The drift verdict is deliberately informational: an adopted canvas carries name/location
/// only - no editable ARM intent - so a stale design name never becomes an ARM write; the
/// fix is a refresh. Delete/update-to-Azure verbs belong to the gated deploy path and [13].
/// Pure and deterministic: design nodes without ids never match; live ids dedupe
/// case-insensitively; rows in ordinal id order.
/// </summary>
public static class ReconcileEngine
{
    public static ReconciliationReport Reconcile(DiagramGraph intent, IEnumerable<AdoptableResource> live)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(live);

        // Trimmed ARM ids of design nodes, case-insensitive, plus the design-side name/location
        // the live facts are compared against. Nodes without ids are hand-made: untracked.
        var design = new Dictionary<string, AzureNodeDto>(StringComparer.OrdinalIgnoreCase);
        var untracked = 0;
        foreach (var dto in intent.Nodes.Concat(intent.Groups).OfType<AzureNodeDto>())
        {
            var id = LivePlaneMerger.Identity(dto);
            if (id.Length == 0) { untracked++; continue; }
            design[id] = dto;
        }

        var rows = new List<ReconcileRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in live
                     .Where(r => !string.IsNullOrWhiteSpace(r.Id) && !string.IsNullOrWhiteSpace(r.Type))
                     .Select(r => r with { Id = r.Id.Trim().TrimStart('/') })
                     .Where(r => r.Id.Length > 0)
                     .GroupBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
                     .Select(g => g.OrderBy(r => r.Type, StringComparer.Ordinal).ThenBy(r => r.Name, StringComparer.Ordinal).First())
                     .OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            seen.Add(resource.Id);
            var state = DesignState.InLiveNotDesign;
            string? designName = null;
            if (design.TryGetValue(resource.Id, out var node))
            {
                designName = node.Name;
                var nameDrift = node.Name.Length > 0 && !string.Equals(node.Name, resource.Name, StringComparison.Ordinal);
                var locationDrift = node.Location.Length > 0
                    && !string.IsNullOrWhiteSpace(resource.Location)
                    && !string.Equals(node.Location, resource.Location, StringComparison.OrdinalIgnoreCase);
                state = nameDrift || locationDrift ? DesignState.Drifted : DesignState.InBothSynced;
            }
            rows.Add(new ReconcileRow(resource.Id, resource.Type, resource.Name, resource.Location, state, designName));
        }

        var designOnly = design.Keys.Where(id => !seen.Contains(id)).OrderBy(id => id, StringComparer.Ordinal).ToList();
        return new ReconciliationReport(rows, designOnly, untracked);
    }
}
