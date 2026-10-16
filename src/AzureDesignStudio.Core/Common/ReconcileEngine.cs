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

    // ---- per-resource reconcile verbs [58]: design-side edits only; neither issues an Azure call. ----

    /// <summary>
    /// The refresh verb for one <see cref="DesignState.Drifted"/> row: the matched design node
    /// takes the live name (and the live location when the scan row carries one); identity,
    /// position, frame membership and links are intent and stay untouched. The live side is the
    /// scan snapshot the drawer already shows - nothing is re-queried from Azure.
    /// Deterministic and pure: the input graph is never mutated; a no-match or already-agreeing
    /// pair returns a fresh copy with <c>Changed = false</c>.
    /// </summary>
    public static (DiagramGraph Graph, bool Changed) RefreshFromLive(DiagramGraph intent, AdoptableResource live)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(live);

        var id = (live.Id ?? string.Empty).Trim().TrimStart('/');
        var graph = LivePlaneMerger.Clone(intent);
        if (id.Length == 0)
            return (graph, false);

        var node = Match(graph, id);
        if (node is null)
            return (graph, false);

        var changed = false;
        if (!string.IsNullOrEmpty(live.Name) && !string.Equals(node.Name, live.Name, StringComparison.Ordinal))
        {
            node.Name = live.Name;
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(live.Location) &&
            !string.Equals(node.Location, live.Location, StringComparison.OrdinalIgnoreCase))
        {
            node.Location = live.Location;
            changed = true;
        }
        return (graph, changed);
    }

    /// <summary>
    /// The detach verb for <see cref="ReconciliationReport.DesignOnly"/> rows: the named design
    /// nodes lose their live identity so reconcile stops pairing them with Azure - ArmId cleared,
    /// and for legacy designs that carry the ARM id in <see cref="AzureNodeDto.Id"/> the id is
    /// rewritten to a deterministic placeholder with links and frame membership remapped. The
    /// node itself STAYS on the canvas as pure intent; nothing is deleted and no Azure call is
    /// issued - destroying live resources is the gated deploy/decommission path's verb, not this
    /// one. Deterministic and pure: same input, same output ids.
    /// </summary>
    public static (DiagramGraph Graph, IReadOnlyList<string> Detached) Detach(DiagramGraph intent, IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(intent);
        ArgumentNullException.ThrowIfNull(ids);

        var targets = new HashSet<string>(
            ids.Select(i => (i ?? string.Empty).Trim().TrimStart('/').TrimEnd('/')).Where(i => i.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        var graph = LivePlaneMerger.Clone(intent);
        if (targets.Count == 0)
            return (graph, []);

        var detached = new SortedSet<string>(StringComparer.Ordinal);
        var renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var next = 1;
        foreach (var node in graph.Nodes.Concat(graph.Groups).OfType<AzureNodeDto>())
        {
            var identity = LivePlaneMerger.Identity(node);
            if (identity.Length == 0 || !targets.Contains(identity))
                continue;
            node.ArmId = string.Empty;
            if (targets.Contains(node.Id) && node.Id.Contains("/providers/", StringComparison.OrdinalIgnoreCase))
            {
                // Legacy identity lives in Id: honest detach means rewriting it, and every
                // endpoint that referenced it (links, child frame membership).
                var old = node.Id;
                string fresh;
                do { fresh = $"detached-{next++}"; } while (Occupied(graph, fresh));
                node.Id = fresh;
                renames[old] = fresh;
            }
            detached.Add(identity.Trim().TrimStart('/'));
        }

        if (renames.Count > 0)
        {
            foreach (var node in graph.Nodes.Concat(graph.Groups).OfType<AzureNodeDto>())
                if (node.GroupId.Length > 0 && renames.TryGetValue(node.GroupId, out var to))
                    node.GroupId = to;
            graph.Links = graph.Links.Select(l => new LinkModelDto
            {
                SourcePortParentId = renames.TryGetValue(l.SourcePortParentId, out var s) ? s : l.SourcePortParentId,
                SourcePortAlignment = l.SourcePortAlignment,
                TargetPortParentId = renames.TryGetValue(l.TargetPortParentId, out var t) ? t : l.TargetPortParentId,
                TargetPortAlignment = l.TargetPortAlignment,
            }).ToList();
        }
        return (graph, detached.ToList());
    }

    private static AzureNodeDto? Match(DiagramGraph graph, string id) =>
        graph.Nodes.Concat(graph.Groups).OfType<AzureNodeDto>()
            .LastOrDefault(n =>
            {
                var identity = LivePlaneMerger.Identity(n);
                return identity.Length > 0 && string.Equals(identity, id, StringComparison.OrdinalIgnoreCase);
            });

    private static bool Occupied(DiagramGraph graph, string id) =>
        graph.Nodes.Concat(graph.Groups).OfType<AzureNodeDto>()
            .Any(n => string.Equals(n.Id, id, StringComparison.OrdinalIgnoreCase));
}
