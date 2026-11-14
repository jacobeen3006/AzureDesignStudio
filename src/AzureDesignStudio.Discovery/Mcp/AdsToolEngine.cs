using AzureDesignStudio.Discovery.Analysis;
using AzureDesignStudio.Discovery.Decommission;
using AzureDesignStudio.Discovery.Drift;
using AzureDesignStudio.Discovery.Exposure;
using AzureDesignStudio.Discovery.Governance;
using AzureDesignStudio.Discovery.Inventory;
using AzureDesignStudio.Discovery.Scan;
using AzureDesignStudio.Discovery.Unmanaged;

namespace AzureDesignStudio.Discovery.Mcp;

/// <summary>
/// Pure read-only engine behind the local MCP server (v2 [93]): every method reads the
/// point-in-time snapshot the host hands it and never touches the workspace DB, ARM or the
/// gRPC server. All ordering is deterministic (ordinal / ordinal-ignore-case) and every
/// timestamp is injected, so the same snapshot serializes identically across calls.
/// </summary>
public static class AdsToolEngine
{
    private static readonly ScanDiffer Differ = new();

    /// <summary>Honest coverage limits of the v1 exposure rules - every consumer (MCP, scan drawer) shows these verbatim.</summary>
    public static readonly string[] ExposureCaveats =
    [
        "coverage: only the v1 rule set is evaluated (VM public IPs, NSG inbound rules, application gateways, load balancers, SQL server firewalls, private endpoint approvals, and seven PaaS endpoint types); other services are not checked",
        "links that cannot be resolved within the snapshot (e.g. cross-subscription ids) are skipped without a claim, never assumed safe",
        "a NIC without an attached NSG is not judged for admin-port exposure (platform defaults vary by placement)",
    ];

    /// <summary>Honest evidence limits of the v1 management audit - every consumer shows these verbatim.</summary>
    public static readonly string[] UnmanagedCaveats =
    [
        "evidence: only the ARM managedBy property, ownership tags (providers/managedby, managed-by, provisioned-by) and VMSS membership count as orchestration markers; other control planes (stacks or Terraform without a marker) are not detected",
        "protection: judged by the supplied guardrail policy only; a resource the policy does not claim is not protected",
    ];

    /// <summary>
    /// This tool has no live lock / RBAC / backup evidence, so these stand on every decom
    /// plan - an explicit "was not evaluated", never a clean bill.
    /// </summary>
    private static readonly string[] NotEvaluatedCaveats =
    [
        "locks-not-evaluated: no resource-lock evidence was supplied to this plan",
        "rbac-not-evaluated: no role-assignment evidence was supplied to this plan",
        "backups-not-evaluated: no backup-protection evidence was supplied to this plan",
    ];

    public static InventoryPage InventoryQuery(IReadOnlyList<InventoryItem> items, InventoryFilter filter, int limit, int offset)
    {
        var lim = Math.Max(0, limit);
        var off = Math.Max(0, offset);

        var filtered = items
            .Where(i => filter.TypeContains is null || i.Type.Contains(filter.TypeContains, StringComparison.OrdinalIgnoreCase))
            .Where(i => filter.ScopePrefix is null || i.Id.StartsWith(filter.ScopePrefix, StringComparison.OrdinalIgnoreCase))
            .Where(i => filter.TagKey is null || TagOf(i, filter.TagKey) is not null)
            .Where(i => filter.TagValue is null || filter.TagKey is null
                || string.Equals(TagOf(i, filter.TagKey), filter.TagValue, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var total = filtered.Count;
        var rows = filtered.Skip(off).Take(lim).Select(ToView).ToList();
        return new InventoryPage(rows, total, lim, off, off + lim < total);
    }

    public static IReadOnlyList<TypeCount> TypeSummary(IReadOnlyList<InventoryItem> items) =>
        items.GroupBy(i => i.Type, StringComparer.OrdinalIgnoreCase)
            .Select(g => new TypeCount(g.Key.ToLowerInvariant(), g.Count()))
            .OrderBy(t => t.Type, StringComparer.Ordinal)
            .ToList();

    public static DiffSummary ScanDiff(IReadOnlyList<InventoryItem> before, IReadOnlyList<InventoryItem> after, int topN)
    {
        var diff = Differ.Diff(before, after);
        var changed = diff.Drift.Count(d => d.Kind == ResourceChangeKind.Changed);
        var propertyDiffs = diff.Drift
            .Where(d => d.Kind == ResourceChangeKind.Changed)
            .Sum(d => d.Properties.Count);

        return new DiffSummary(
            diff.Added,
            diff.Removed,
            changed,
            diff.ChangedResources,
            changed - diff.ChangedResources,
            propertyDiffs,
            diff.Drift.Take(Math.Max(0, topN)).Select(ToRow).ToList(),
            diff.BeforeHash,
            diff.AfterHash);
    }

    public static ExposureReport Exposure(IReadOnlyList<InventoryItem> items)
    {
        var findings = ExposureAnalyzer.Analyze(items);
        return new ExposureReport(
            findings.Count(f => f.Severity == ExposureSeverity.Critical),
            findings.Count(f => f.Severity == ExposureSeverity.High),
            findings.Count(f => f.Severity == ExposureSeverity.Medium),
            findings.Count(f => f.Severity == ExposureSeverity.Low),
            findings.Select(f => new ExposureRowView(
                f.ResourceId, f.ResourceType, f.Kind, f.Severity.ToString(), f.Detail, f.Remediation)).ToList(),
            ExposureCaveats);
    }

    public static UnmanagedReport Unmanaged(IReadOnlyList<InventoryItem> items, GuardrailPolicy policy)
    {
        var audit = UnmanagedDetector.Audit(items, policy);
        return new UnmanagedReport(
            audit.Unmanaged.Select(f => f.Id).ToList(),
            audit.ProtectedResources.Select(f => new ProtectedRow(f.Id, f.Type, f.Reason)).ToList(),
            items.Count - audit.Unmanaged.Count - audit.ProtectedResources.Count,
            UnmanagedCaveats);
    }

    /// <summary>
    /// [16] "what can reach this resource?" over one snapshot: the engine's answer verbatim -
    /// proven paths, never a closure claim. No RBAC evidence exists in the scan plane, so the
    /// principals answer stays caveated, not guessed.
    /// </summary>
    public static ReachReport Reach(IReadOnlyList<InventoryItem> items, string targetId)
    {
        var answer = ReachabilityEngine.Analyze(targetId, items);
        return new ReachReport(
            answer.TargetId,
            answer.InternetReachable,
            answer.Paths.Select(p => new ReachPathView(p.Origin.ToString(), p.Via, p.Hops, p.Evidence)).ToList(),
            answer.Principals,
            answer.Caveats,
            answer.Diagnostics.Select(d => new ReachDiagnosticView(d.Severity.ToString(), d.Code, d.Message)).ToList());
    }

    public static DecomReport DecomPlan(IReadOnlyList<InventoryItem> items, IReadOnlyList<string> candidates, GuardrailPolicy policy, DateTimeOffset now, TimeSpan purgeDelay)
    {
        // No live lock / RBAC / backup evidence exists for this tool: empty (not null) lists
        // keep the planner's per-resource checks off, and the NotEvaluatedCaveats below say so.
        var plan = DecommissionPlanner.Plan(items, candidates,
            guardrails: policy, locks: [], roleAssignments: [], backupItems: []);

        string? planHash = null;
        string? windowId = null;
        DateTimeOffset? purgeNotBefore = null;
        if (plan.Allowed)
        {
            if (purgeDelay <= TimeSpan.Zero)
                throw new ArgumentException("purgeDelay must be positive - the undo window is the point", nameof(purgeDelay));
            var runbook = DecommissionRunbook.Generate(plan, items, new RunbookOptions(now, purgeDelay, ExistingLocks: []));
            planHash = runbook.PlanHash;
            windowId = runbook.Window.WindowId;
            purgeNotBefore = runbook.PurgeNotBefore;
        }
        // Refused: the 13.4 rule - a refused plan generates no runbook identity at all.

        return new DecomReport(
            plan.Allowed,
            plan.Stages,
            plan.Blockers.Where(b => b.Severity == DecommissionSeverity.Refusal)
                .Select(b => new RefusalView(b.ResourceId, b.Kind, b.Detail)).ToList(),
            plan.Blockers.Select(b => new BlockerView(b.ResourceId, b.Kind, b.Severity.ToString(), b.Detail)).ToList(),
            plan.Impacts.Select(i => new ImpactView(i.ResourceId, i.Kind, i.Severity.ToString(), i.Detail)).ToList(),
            planHash,
            windowId,
            purgeNotBefore,
            plan.Caveats.Concat(NotEvaluatedCaveats).ToList());
    }

    /// <summary>The state of a source on a scan whose manifest predates governance collection.</summary>
    public const string NotCollected = "not collected";

    /// <summary>The state of a source the scan's manifest does not answer for (no readable manifest, or no entry).</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// Governance findings stored with one scan (Wave B3), read together with the state of each
    /// source from the scan's <paramref name="manifest"/>, so that a source that was not collected
    /// in full never reads as "no findings". <paramref name="source"/> (advisor, policy or defender,
    /// any case) and <paramref name="resourceId"/> (the whole ARM id, any case) filter the rows;
    /// blank means off, and an unknown source name throws <see cref="ArgumentException"/>.
    /// <paramref name="limit"/> is clamped to 1..1000. <paramref name="malformed"/> is how many
    /// stored rows the host could not read; the caveats say so.
    /// </summary>
    public static GovernanceReport Governance(ScanManifest? manifest, IReadOnlyList<GovernanceFinding> rows, int malformed,
        string? source, string? resourceId, int limit)
    {
        ArgumentNullException.ThrowIfNull(rows);

        string? only = null;
        if (!string.IsNullOrWhiteSpace(source))
            only = GovernanceSources.All.FirstOrDefault(s => s.Equals(source.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException(
                    $"unknown governance source '{source}': use advisor, policy or defender", nameof(source));
        var id = string.IsNullOrWhiteSpace(resourceId) ? null : resourceId.Trim();
        var lim = Math.Clamp(limit, 1, 1000);

        var stored = rows.Where(r => r is not null).ToList();
        var matching = stored
            .Where(r => only is null || KeyOf(r.Source) == only)
            .Where(r => id is null || string.Equals(r.ResourceId, id, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.Severity)
            .ThenBy(r => r.ResourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Source)
            .ThenBy(r => r.Title, StringComparer.Ordinal)
            // Ties broken ordinally; a record id is unique per source within a scan, so the order is total.
            .ThenBy(r => r.ResourceId, StringComparer.Ordinal)
            .ThenBy(r => r.RecordId, StringComparer.Ordinal)
            .ToList();

        var sources = GovernanceSources.All
            .Select(key =>
            {
                var (state, detail) = StateOf(manifest, key);
                return new GovernanceSourceView(key, state, detail, stored.Count(r => KeyOf(r.Source) == key));
            })
            .ToList();

        var caveats = new List<string>(GovernanceCaveats.All);
        foreach (var s in sources)
            if (s.State != SourceState.Collected && (only is null || s.Source == only))
                caveats.Add($"{s.Source} is {s.State} for this scan, so a missing {s.Source} finding is not proof of none.");
        if (malformed > 0)
            caveats.Add($"{malformed} malformed governance row(s) skipped (unknown source or severity).");

        return new GovernanceReport(
            sources,
            new GovernanceSourceCounts(
                matching.Count(r => r.Source == GovernanceSource.Advisor),
                matching.Count(r => r.Source == GovernanceSource.Policy),
                matching.Count(r => r.Source == GovernanceSource.Defender)),
            new GovernanceSeverityCounts(
                matching.Count(r => r.Severity == GovernanceSeverity.High),
                matching.Count(r => r.Severity == GovernanceSeverity.Medium),
                matching.Count(r => r.Severity == GovernanceSeverity.Low)),
            matching.Take(lim).Select(ToView).ToList(),
            matching.Count,
            matching.Count > lim,
            caveats);
    }

    private static (string State, string? Detail) StateOf(ScanManifest? manifest, string source)
    {
        if (manifest is null)
            return (Unknown, "The scan has no readable manifest (it is still running, was interrupted, or predates manifests), so how far this source got is unknown.");
        if (manifest.Sources is null)
            return (NotCollected, "The scan predates governance collection.");
        return manifest.Sources.TryGetValue(source, out var state)
            ? (state.State, state.Detail)
            : (Unknown, "The scan manifest does not mention this source.");
    }

    /// <summary>The stored source name (<see cref="GovernanceSources"/>) of a finding's source.</summary>
    private static string KeyOf(GovernanceSource source) => source switch
    {
        GovernanceSource.Advisor => GovernanceSources.Advisor,
        GovernanceSource.Policy => GovernanceSources.Policy,
        GovernanceSource.Defender => GovernanceSources.Defender,
        _ => source.ToString().ToLowerInvariant(),
    };

    private static GovernanceRowView ToView(GovernanceFinding f) =>
        new(KeyOf(f.Source), f.Severity.ToString(), f.ResourceId, f.SubscriptionId, f.Category, f.Title, f.Detail,
            f.PolicyAssignmentId, f.PolicyDefinitionId, f.RecordId);

    private static InventoryItemView ToView(InventoryItem i) =>
        new(i.Id, i.Name, i.Type, i.Location, i.ResourceGroup, i.SubscriptionId, i.Tags);

    private static DiffRowView ToRow(ResourceDrift d) =>
        new(d.Kind.ToString(), d.ResourceId, d.Kind switch
        {
            ResourceChangeKind.Changed => d.Properties.Count == 1
                ? $"1 property changed: {d.Properties[0].Path}"
                : $"{d.Properties.Count} properties changed: "
                    + string.Join(", ", d.Properties.Take(3).Select(p => p.Path))
                    + (d.Properties.Count > 3 ? ", ..." : ""),
            _ => d.ResourceType,
        });

    private static string? TagOf(InventoryItem item, string key)
    {
        foreach (var kv in item.Tags)
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }
}
