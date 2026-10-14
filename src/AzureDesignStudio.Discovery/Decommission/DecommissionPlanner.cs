using System.Text.Json;
using AzureDesignStudio.Discovery.Analysis;
using AzureDesignStudio.Discovery.Inventory;
using AzureDesignStudio.Discovery.Unmanaged;

namespace AzureDesignStudio.Discovery.Decommission;

/// <summary>How strongly a finding gates the plan: <see cref="DecommissionSeverity.Refusal"/> blocks it.</summary>
public enum DecommissionSeverity { Info, Warning, Refusal }

/// <summary>Resource lock as ARM reports it; the level mirrors the Microsoft.Authorization lock enum.</summary>
public enum ArmLockLevel { NotSpecified, CanNotDelete, CanNotDeleteAndDenyAssignments, DenyAssignments }

public sealed record ArmLock(string Scope, ArmLockLevel Level, string Name);

/// <summary>RBAC assignment; only assignments scoped exactly to a resource are removed with it
/// (assignments on a parent scope outlive the resource and are not this plan's concern).</summary>
public sealed record ScopeRoleAssignment(string Scope, string RoleName, string PrincipalName);

/// <summary>Backup protection fact: one protected resource inside one recovery vault.</summary>
public sealed record BackupProtectionItem(string VaultId, string ProtectedResourceId);

/// <summary>Why the plan cannot (or should not) proceed, per resource, with a stable code and a human detail.</summary>
public sealed record DecommissionBlocker(string ResourceId, string Kind, DecommissionSeverity Severity, string Detail);

/// <summary>An irreversible or visible consequence of the delete - the impact preview half of [65].</summary>
public sealed record DecommissionImpact(string ResourceId, string Kind, DecommissionSeverity Severity, string Detail);

/// <summary>A child the scan holds that dies with its parent even though it was not selected.</summary>
public sealed record ImplicitCascade(string ParentId, string ChildId, string ChildType);

/// <summary>Full planner answer: staged order plus every blocker, impact and honest caveat.</summary>
public sealed class DecommissionPlan
{
    public required IReadOnlyList<string> Candidates { get; init; }

    /// <summary>Ordered batches: every id in a stage is safe to delete once all earlier stages are done.</summary>
    public required IReadOnlyList<IReadOnlyList<string>> Stages { get; init; }

    public required IReadOnlyList<DecommissionBlocker> Blockers { get; init; }
    public required IReadOnlyList<DecommissionImpact> Impacts { get; init; }
    public required IReadOnlyList<ImplicitCascade> ImplicitChildren { get; init; }
    public required IReadOnlyList<string> Caveats { get; init; }

    /// <summary>Nothing may execute while any refusal stands.</summary>
    public bool Allowed => Blockers.All(b => b.Severity != DecommissionSeverity.Refusal);
}

/// <summary>
/// Decommission planner (v2 plan 13.2, items [63][64][65]) - pure and offline over one scan.
/// The dependency subgraph reuses the 8.1 derivation (config-carried ARM ids only), so ordering and
/// refusals rest on the same edges the topology draws. Anything ARM reports outside the ARG projection
/// (locks, RBAC, backup items) travels as caller-supplied evidence: a null list is an explicit
/// "was not evaluated" caveat, never a silent clean bill. Output is deterministic: every collection
/// ordinal-sorted, stage picking stable, so two runs over the same scan serialize identically.
/// </summary>
public static class DecommissionPlanner
{
    /// <summary>Kinds where a live reference makes deleting the target fail or break the survivor.</summary>
    private static readonly TopologyEdgeKind[] BlockingKinds =
    [
        TopologyEdgeKind.Uses, TopologyEdgeKind.InSubnet, TopologyEdgeKind.ProtectedBy,
        TopologyEdgeKind.RoutesVia, TopologyEdgeKind.LoadBalances, TopologyEdgeKind.HostedIn,
    ];

    /// <summary>Kinds where deleting the source leaves the external target alive but unattached (billable orphan).</summary>
    private static readonly TopologyEdgeKind[] OrphanSurvivorKinds =
    [
        TopologyEdgeKind.Uses, TopologyEdgeKind.LoadBalances,
    ];

    public static DecommissionPlan Plan(
        IReadOnlyList<InventoryItem> inventory,
        IEnumerable<string> candidateIds,
        GuardrailPolicy? guardrails = null,
        IReadOnlyList<ArmLock>? locks = null,
        IReadOnlyList<ScopeRoleAssignment>? roleAssignments = null,
        IReadOnlyList<BackupProtectionItem>? backupItems = null)
    {
        var byId = new Dictionary<string, InventoryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in inventory)
            byId.TryAdd(item.Id.Trim(), item);

        // Candidates: trimmed, case-insensitive distinct, ordinal-sorted - output never depends on input order.
        var candidates = candidateIds
            .Select(c => c.Trim())
            .Where(c => c.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.Ordinal)
            .ToList();

        var known = new Dictionary<string, InventoryItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in candidates)
            if (byId.TryGetValue(c, out var item)) known[c] = item;

        string Canonical(string id) =>
            byId.TryGetValue(id.Trim(), out var hit) ? hit.Id : id.Trim();

        var graph = TopologyDeriver.Derive(inventory);

        var blockers = new List<DecommissionBlocker>();
        var impacts = new List<DecommissionImpact>();
        var caveats = new List<string>();

        // --- per-candidate facts: guardrails, evidence, static irreversibility ---

        foreach (var id in candidates)
        {
            if (!byId.TryGetValue(id, out var item))
            {
                blockers.Add(new DecommissionBlocker(Canonical(id), "candidate-not-in-scan", DecommissionSeverity.Refusal,
                    "the resource is not in the scanned inventory - re-scan or decommission its parent instead"));
                continue;
            }

            var reason = guardrails?.ProtectionReason(item);
            if (reason is not null)
                blockers.Add(new DecommissionBlocker(item.Id, "guardrail", DecommissionSeverity.Refusal,
                    $"protected by guardrail \"{reason}\" - remove it from the do-not-touch list first"));

            if (locks is not null)
                foreach (var lockEntry in locks)
                {
                    if (!ScopeCovers(lockEntry.Scope, item.Id)) continue;
                    var where = string.Equals(lockEntry.Scope.Trim().TrimEnd('/'), item.Id, StringComparison.OrdinalIgnoreCase)
                        ? "on this scope" : $"inherited from scope {lockEntry.Scope.Trim()}";
                    if (lockEntry.Level is ArmLockLevel.CanNotDelete or ArmLockLevel.CanNotDeleteAndDenyAssignments)
                        blockers.Add(new DecommissionBlocker(item.Id, "lock-can-not-delete", DecommissionSeverity.Refusal,
                            $"lock \"{lockEntry.Name}\" ({lockEntry.Level}) is set {where} - remove it before deleting"));
                    else if (lockEntry.Level == ArmLockLevel.DenyAssignments)
                        blockers.Add(new DecommissionBlocker(item.Id, "lock-deny-assignments", DecommissionSeverity.Info,
                            $"lock \"{lockEntry.Name}\" (DenyAssignments) is set {where} - it blocks assignment changes, not the delete"));
                }

            if (roleAssignments is not null)
            {
                var onResource = roleAssignments
                    .Where(a => string.Equals(a.Scope.Trim().TrimEnd('/'), item.Id, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (onResource.Count > 0)
                    impacts.Add(new DecommissionImpact(item.Id, "rbac-assignments", DecommissionSeverity.Info,
                        $"{onResource.Count} role assignment(s) scoped to this resource are removed with it: " +
                        string.Join("; ", onResource.Select(a => $"{a.RoleName} - {a.PrincipalName}"))));
            }

            if (backupItems is not null)
            {
                var protecting = backupItems
                    .Where(b => string.Equals(b.ProtectedResourceId.Trim(), item.Id, StringComparison.OrdinalIgnoreCase))
                    .Select(b => b.VaultId.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (protecting.Count > 0)
                    blockers.Add(new DecommissionBlocker(item.Id, "backup-protected", DecommissionSeverity.Refusal,
                        $"a backup item protects this resource (vault {string.Join(", ", protecting)}) - stop protection and delete the recovery points first"));

                if (item.Type.Equals("microsoft.recoveryservices/vaults", StringComparison.OrdinalIgnoreCase))
                {
                    var inside = backupItems
                        .Count(b => string.Equals(b.VaultId.Trim(), item.Id, StringComparison.OrdinalIgnoreCase));
                    if (inside > 0)
                        blockers.Add(new DecommissionBlocker(item.Id, "backup-vault-nonempty", DecommissionSeverity.Refusal,
                            $"the vault still protects {inside} item(s); deleting it destroys their recovery points irreversibly"));
                }
            }

            AddStaticFacts(item, impacts);
        }

        // --- cascade: children the scan holds (and materialized subnets) die with a selected parent ---

        var cascadeOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var cascades = new List<ImplicitCascade>();
        var nodeLookup = new Dictionary<string, TopologyNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in graph.Nodes)
            nodeLookup.TryAdd(kv.Key.Trim(), kv.Value);

        foreach (var node in graph.Nodes.Values)
        {
            var nodeId = node.Id.Trim();
            if (known.ContainsKey(nodeId)) continue;
            var owner = OwnerOf(node.Id);
            if (owner is null) continue;
            cascadeOwner[nodeId] = owner;
            cascades.Add(new ImplicitCascade(Canonical(owner), node.Id, node.Type));
        }
        string? OwnerOf(string nodeId)
        {
            var current = nodeId.Trim();
            while (nodeLookup.TryGetValue(current, out var n) && n.ParentId is { } parent)
            {
                var trimmed = parent.Trim();
                if (known.ContainsKey(trimmed)) return trimmed;
                current = trimmed;
            }
            return null;
        }

        bool IsDisk(string id) =>
            nodeLookup.TryGetValue(id, out var n) && n.Type.Equals("microsoft.compute/disks", StringComparison.OrdinalIgnoreCase);

        // An edge that touches a cascade child touches its selected parent; a child is never staged on its own.
        string Collapse(string nodeId)
        {
            var trimmed = nodeId.Trim();
            if (known.ContainsKey(trimmed)) return trimmed;
            return cascadeOwner.TryGetValue(trimmed, out var owner) ? owner : trimmed;
        }

        // --- edges: staged precedence inside the set, external references across its boundary ---

        var precedence = new HashSet<(string Before, string After)>(OrdinalIgnoreCasePairComparer.Instance);
        var external = new Dictionary<(string Candidate, TopologyEdgeKind Kind, string External), (DecommissionSeverity Severity, string Detail)>(
            ExternalKeyComparer.Instance);

        foreach (var edge in graph.Edges
                     .OrderBy(e => e.From, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(e => e.To, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(e => e.Kind)
                     .ThenBy(e => e.Detail, StringComparer.Ordinal))
        {
            var f = Collapse(edge.From);
            var t = Collapse(edge.To);
            if (string.Equals(f, t, StringComparison.OrdinalIgnoreCase)) continue;

            var fKnown = known.ContainsKey(f);
            var tKnown = known.ContainsKey(t);

            if (fKnown && tKnown)
            {
                if (TryOrder(edge.Kind, f, t, IsDisk, out var pair)) precedence.Add(pair);
                continue;
            }

            var (candidate, severity, detail) = ClassifyExternal(edge.Kind, f, t, fKnown, tKnown, IsDisk);
            if (severity is null || detail is null) continue;
            var key = (candidate!, edge.Kind, (tKnown ? f : t).Trim().ToLowerInvariant());
            if (!external.ContainsKey(key))
                external[key] = (severity.Value, detail);
        }

        foreach (var ((candidate, kind, _), (severity, detail)) in external)
        {
            var resource = Canonical(candidate);
            var code = ExternalCode(kind, severity);
            if (severity == DecommissionSeverity.Refusal)
                blockers.Add(new DecommissionBlocker(resource, code, DecommissionSeverity.Refusal, detail));
            else
                impacts.Add(new DecommissionImpact(resource, code, severity, detail));
        }

        // --- staged order: Kahn topological batches over the explicit set, ordinal-stable picking ---

        var stages = new List<IReadOnlyList<string>>();
        var remaining = new HashSet<string>(known.Keys, StringComparer.OrdinalIgnoreCase);
        var incoming = remaining.ToDictionary(id => id, id => 0, StringComparer.OrdinalIgnoreCase);
        foreach (var (before, after) in precedence)
            if (remaining.Contains(before) && remaining.Contains(after))
                incoming[after]++;

        while (remaining.Count > 0)
        {
            var stage = remaining
                .Where(id => incoming[id] == 0)
                .OrderBy(id => Canonical(id), StringComparer.Ordinal)
                .ToList();
            if (stage.Count == 0) break;

            foreach (var id in stage)
            {
                remaining.Remove(id);
                foreach (var (before, after) in precedence)
                    if (string.Equals(before, id, StringComparison.OrdinalIgnoreCase) && incoming.ContainsKey(after) && remaining.Contains(after))
                        incoming[after]--;
            }
            stages.Add(stage.Select(Canonical).ToList());
        }

        if (remaining.Count > 0)
            blockers.Add(new DecommissionBlocker(
                Canonical(remaining.OrderBy(id => Canonical(id), StringComparer.Ordinal).First()),
                "dependency-cycle", DecommissionSeverity.Refusal,
                "cyclic delete order: " + DescribeCycle(remaining, precedence, Canonical) +
                " - the plan refuses; break the cycle (or select the whole group) first"));

        if (guardrails is null) caveats.Add("guardrails-not-supplied: do-not-touch rules were not evaluated");
        if (locks is null) caveats.Add("locks-not-supplied: no resource locks were evaluated");
        if (roleAssignments is null) caveats.Add("role-assignments-not-supplied: RBAC was not evaluated");
        if (backupItems is null) caveats.Add("backup-items-not-supplied: backup protection was not evaluated");

        return new DecommissionPlan
        {
            Candidates = candidates.Select(Canonical).ToList(),
            Stages = stages,
            Blockers = blockers
                .Distinct()
                .OrderBy(b => b.ResourceId, StringComparer.Ordinal)
                .ThenBy(b => b.Kind, StringComparer.Ordinal)
                .ThenBy(b => b.Detail, StringComparer.Ordinal)
                .ToList(),
            Impacts = impacts
                .Distinct()
                .OrderBy(i => i.ResourceId, StringComparer.Ordinal)
                .ThenBy(i => i.Kind, StringComparer.Ordinal)
                .ThenBy(i => i.Detail, StringComparer.Ordinal)
                .ToList(),
            ImplicitChildren = cascades
                .OrderBy(c => c.ParentId, StringComparer.Ordinal)
                .ThenBy(c => c.ChildId, StringComparer.Ordinal)
                .ToList(),
            Caveats = caveats,
        };
    }

    // --- ordering rules ---

    /// <summary>Which side of a reference must go first: the referencing resource, except attachments
    /// where the disk must outlive the VM that holds its lease. Peerings impose no order.</summary>
    private static bool TryOrder(TopologyEdgeKind kind, string from, string to, Func<string, bool> isDisk, out (string Before, string After) pair)
    {
        switch (kind)
        {
            case TopologyEdgeKind.PeersWith:
                pair = default;
                return false;
            case TopologyEdgeKind.AttachedTo:
                var fromDisk = isDisk(from);
                var toDisk = isDisk(to);
                if (fromDisk == toDisk)
                {
                    pair = default;
                    return false;
                }
                pair = fromDisk ? (to, from) : (from, to);
                return true;
            default:
                pair = (from, to);
                return true;
        }
    }

    // --- external-reference verdicts [64] ---

    private static (string? Candidate, DecommissionSeverity? Severity, string? Detail) ClassifyExternal(
        TopologyEdgeKind kind, string from, string to, bool fromKnown, bool toKnown,
        Func<string, bool> isDisk)
    {
        // The candidate is the scanned side; the other endpoint keeps living outside the delete set.
        if (toKnown)
        {
            switch (kind)
            {
                case var k when BlockingKinds.Contains(k):
                    return (to, DecommissionSeverity.Refusal,
                        $"referenced by {from} ({kind}): delete or detach the referencing resource first");
                case TopologyEdgeKind.AttachedTo:
                    return isDisk(to)
                        ? (to, DecommissionSeverity.Refusal,
                            $"the disk is still attached to {from}: delete the owning resource first")
                        : (to, DecommissionSeverity.Warning,
                            $"deleting this resource detaches disk {from}, which survives unattached");
                case TopologyEdgeKind.ConnectsTo:
                    return (to, DecommissionSeverity.Warning,
                        $"private endpoint {from} loses its service and its connection moves to Disconnected");
                case TopologyEdgeKind.DiagnosticsTo:
                    return (to, DecommissionSeverity.Warning,
                        $"{from} keeps a diagnostic setting pointing at the deleted target");
                case TopologyEdgeKind.PeersWith:
                    return (to, DecommissionSeverity.Warning,
                        $"{from} keeps its peering configuration pointing at the deleted network");
            }
        }
        else if (fromKnown)
        {
            switch (kind)
            {
                case var k when OrphanSurvivorKinds.Contains(k):
                    return (from, DecommissionSeverity.Warning,
                        $"{to} survives this delete and is left unattached (still billable)");
                case TopologyEdgeKind.AttachedTo:
                    return isDisk(from)
                        ? (from, DecommissionSeverity.Refusal,
                            $"the disk is still attached to {to}: delete or detach it from that resource first")
                        : (from, DecommissionSeverity.Warning,
                            $"deleting this resource detaches disk {to}, which survives unattached");
                case TopologyEdgeKind.PeersWith:
                    return (from, DecommissionSeverity.Warning,
                        $"the peering to {to} disappears with this resource");
            }
        }
        return (null, null, null);
    }

    /// <summary>Stable machine-readable code per external reference: AttachedTo splits into
    /// the disk-lease refusal and the surviving-detachment warning by severity.</summary>
    private static string ExternalCode(TopologyEdgeKind kind, DecommissionSeverity severity) => kind switch
    {
        TopologyEdgeKind.AttachedTo => severity == DecommissionSeverity.Refusal ? "disk-attached" : "disks-detach",
        TopologyEdgeKind.ConnectsTo => "private-endpoint-breaks",
        TopologyEdgeKind.DiagnosticsTo => "diagnostics-break",
        TopologyEdgeKind.PeersWith => "peering-removed",
        _ => severity == DecommissionSeverity.Refusal ? "referenced-externally" : "survivor-orphan",
    };

    // --- static irreversibility facts [65] ---

    private static void AddStaticFacts(InventoryItem item, List<DecommissionImpact> impacts)
    {
        var props = item.Raw;
        switch (item.Type.ToLowerInvariant())
        {
            case "microsoft.keyvault/vaults":
                var softDelete = BoolAt(props, "enableSoftDelete", defaultValue: true);
                var days = IntAt(props, "softDeleteRetentionInDays", defaultValue: 90);
                impacts.Add(softDelete
                    ? new DecommissionImpact(item.Id, "keyvault-soft-delete", DecommissionSeverity.Info,
                        $"the vault enters soft-deleted state for {days} days; the name stays reserved until purge")
                    : new DecommissionImpact(item.Id, "keyvault-hard-delete", DecommissionSeverity.Warning,
                        "soft delete is disabled: the delete is immediate and unrecoverable"));
                if (BoolAt(props, "enablePurgeProtection", defaultValue: false))
                    impacts.Add(new DecommissionImpact(item.Id, "keyvault-purge-protection", DecommissionSeverity.Warning,
                        "purge protection is on: the soft-deleted vault can never be purged, and the name stays reserved"));
                break;

            case "microsoft.storage/storageaccounts":
                var immutable = NestedBoolAt(props, "immutableStorageWithVersioning", "enabled")
                                || BoolAt(props, "enableImmutableBlobStorage", defaultValue: false);
                impacts.Add(new DecommissionImpact(item.Id, "storage-data-destroyed", DecommissionSeverity.Warning,
                    immutable
                        ? "immutable storage is enabled: version-protected blobs resist deletion and are destroyed unrecoverably with the account"
                        : "all containers, blobs, queues and tables are destroyed; immutability policies and legal holds live on child scopes this scan does not cover"));
                break;

            case "microsoft.containerregistry/registries":
                impacts.Add(new DecommissionImpact(item.Id, "images-destroyed", DecommissionSeverity.Info,
                    "every image and helm chart is deleted with the registry; there is no soft delete"));
                break;

            case "microsoft.operationalinsights/workspaces":
                impacts.Add(new DecommissionImpact(item.Id, "telemetry-destroyed", DecommissionSeverity.Warning,
                    "all ingested logs are destroyed with the workspace; recovery is only via Microsoft support within 30 days"));
                break;

            case "microsoft.recoveryservices/vaults":
                impacts.Add(new DecommissionImpact(item.Id, "vault-irreversible", DecommissionSeverity.Warning,
                    "vault deletion destroys retained recovery points and backup data irreversibly"));
                break;

            case "microsoft.sql/servers" or "microsoft.dbforpostgresql/servers" or "microsoft.dbforpostgresql/flexibleservers":
                impacts.Add(new DecommissionImpact(item.Id, "backups-destroyed", DecommissionSeverity.Info,
                    "every child database and its backups/point-in-time data are deleted with the server"));
                break;
        }
    }

    // --- ARM-shaped JSON reads: every fact optional, defaults are ARM's documented defaults ---

    private static bool BoolAt(JsonElement? props, string name, bool defaultValue) =>
        props is { } p && p.ValueKind == JsonValueKind.Object &&
        p.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : defaultValue;

    private static int IntAt(JsonElement? props, string name, int defaultValue) =>
        props is { } p && p.ValueKind == JsonValueKind.Object &&
        p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : defaultValue;

    private static bool NestedBoolAt(JsonElement? props, string outer, string inner) =>
        props is { } p && p.ValueKind == JsonValueKind.Object &&
        p.TryGetProperty(outer, out var o) && o.ValueKind == JsonValueKind.Object &&
        o.TryGetProperty(inner, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();

    private static bool ScopeCovers(string scope, string id)
    {
        var s = scope.Trim().TrimEnd('/');
        return string.Equals(s, id, StringComparison.OrdinalIgnoreCase)
               || id.StartsWith(s + "/", StringComparison.OrdinalIgnoreCase);
    }

    private static string DescribeCycle(
        HashSet<string> remaining,
        HashSet<(string Before, string After)> precedence,
        Func<string, string> canonical)
    {
        // Every node left after Kahn has an incoming edge inside the remainder; walking predecessors
        // from the ordinal-smallest id must return to a visited node - that loop is the cycle name.
        string? Prev(string id) => remaining
            .Where(x => precedence.Contains((x, id)) && !string.Equals(x, id, StringComparison.OrdinalIgnoreCase))
            .OrderBy(canonical, StringComparer.Ordinal)
            .FirstOrDefault();

        var start = remaining.OrderBy(canonical, StringComparer.Ordinal).First();
        var path = new List<string>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var current = start;
        while (true)
        {
            if (seen.TryGetValue(current, out var at))
            {
                var cycle = path[at..];
                return string.Join(" -> ", cycle.Select(canonical)) + " -> " + canonical(cycle[0]);
            }
            seen[current] = path.Count;
            path.Add(current);
            var prev = Prev(current);
            if (prev is null) return string.Join(" -> ", path.Select(canonical)); // defensive: chain dead-ends, no loop to name
            current = prev;
        }
    }

    private sealed class OrdinalIgnoreCasePairComparer : IEqualityComparer<(string Before, string After)>
    {
        public static readonly OrdinalIgnoreCasePairComparer Instance = new();

        public bool Equals((string Before, string After) a, (string Before, string After) b) =>
            string.Equals(a.Before, b.Before, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.After, b.After, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Before, string After) v) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(v.Before) * 397 ^ StringComparer.OrdinalIgnoreCase.GetHashCode(v.After);
    }

    private sealed class ExternalKeyComparer : IEqualityComparer<(string Candidate, TopologyEdgeKind Kind, string External)>
    {
        public static readonly ExternalKeyComparer Instance = new();

        public bool Equals((string Candidate, TopologyEdgeKind Kind, string External) a, (string Candidate, TopologyEdgeKind Kind, string External) b) =>
            string.Equals(a.Candidate, b.Candidate, StringComparison.OrdinalIgnoreCase) &&
            a.Kind == b.Kind &&
            string.Equals(a.External, b.External, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Candidate, TopologyEdgeKind Kind, string External) v) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(v.Candidate) * 397 ^
            v.Kind.GetHashCode() * 17 ^
            StringComparer.OrdinalIgnoreCase.GetHashCode(v.External);
    }
}
