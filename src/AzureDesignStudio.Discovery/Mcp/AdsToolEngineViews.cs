namespace AzureDesignStudio.Discovery.Mcp;

/// <summary>
/// <see cref="AdsToolEngine.InventoryQuery"/> filter: every set facet is ANDed; a null facet is off.
/// <see cref="TagValue"/> only takes effect together with <see cref="TagKey"/>.
/// </summary>
public sealed record InventoryFilter(
    string? TypeContains = null,
    string? ScopePrefix = null,
    string? TagKey = null,
    string? TagValue = null)
{
    /// <summary>No facets set - matches every row.</summary>
    public static InventoryFilter None { get; } = new();
};
/// <summary>One inventory row projected for the wire (the raw JSON payload is intentionally not projected).</summary>
public sealed record InventoryItemView(
    string Id,
    string Name,
    string Type,
    string Location,
    string ResourceGroup,
    string SubscriptionId,
    IReadOnlyDictionary<string, string> Tags);

/// <summary>
/// One page of the ordered inventory. <see cref="Total"/> is the post-filter count,
/// <see cref="Truncated"/> says more rows exist beyond this page.
/// </summary>
public sealed record InventoryPage(
    IReadOnlyList<InventoryItemView> Rows,
    int Total,
    int Limit,
    int Offset,
    bool Truncated);

/// <summary>Row count for one (case-insensitively grouped) resource type.</summary>
public sealed record TypeCount(string Type, int Count);

/// <summary>One diff row in the differ's deterministic order; <see cref="Detail"/> is a short human summary.</summary>
public sealed record DiffRowView(string Kind, string Id, string Detail);

/// <summary>
/// Scan-diff summary. <see cref="Changed"/> is every resource with any property difference;
/// <see cref="ChangedDrift"/> is the subset the differ counts as real drift (a resource whose
/// only diffs are volatile platform churn is <see cref="ChangedChurn"/>, never drift).
/// </summary>
public sealed record DiffSummary(
    int Added,
    int Removed,
    int Changed,
    int ChangedDrift,
    int ChangedChurn,
    int PropertyDiffs,
    IReadOnlyList<DiffRowView> Top,
    string BeforeHash,
    string AfterHash);

/// <summary>One internet-exposure finding; <see cref="Severity"/> is the analyzer enum name (Critical/High/Medium/Low).</summary>
public sealed record ExposureRowView(
    string ResourceId,
    string ResourceType,
    string Kind,
    string Severity,
    string Detail,
    string Remediation);

/// <summary>Exposure report: per-severity counts (which always match the row counts) plus honest coverage caveats.</summary>
public sealed record ExposureReport(
    int Critical,
    int High,
    int Medium,
    int Low,
    IReadOnlyList<ExposureRowView> Rows,
    IReadOnlyList<string> Caveats);

/// <summary>
/// Unmanaged-resource audit. <see cref="Protected"/> rows carry the matched guardrail pattern
/// (the detector's reason), so a protection is always explainable, not just a flag.
/// </summary>
public sealed record UnmanagedReport(
    IReadOnlyList<string> UnmanagedIds,
    IReadOnlyList<ProtectedRow> Protected,
    int ManagedCount,
    IReadOnlyList<string> Caveats);

/// <summary>A protected resource and the guardrail pattern that claimed it.</summary>
public sealed record ProtectedRow(string Id, string Type, string Reason);

/// <summary>A plan-level refusal (severity Refusal); <see cref="ResourceId"/> names the referrer.</summary>
public sealed record RefusalView(string ResourceId, string Kind, string Detail);

/// <summary>A planner blocker at any severity (Info/Warning/Refusal).</summary>
public sealed record BlockerView(string ResourceId, string Kind, string Severity, string Detail);

/// <summary>An impact preview row at any severity (Info/Warning/Refusal).</summary>
public sealed record ImpactView(string ResourceId, string Kind, string Severity, string Detail);

/// <summary>
/// Plan-only decommission answer (v2 [93]): the planner's stages, blockers, impacts and caveats
/// plus the runbook identity - <see cref="PlanHash"/>/<see cref="WindowId"/>/<see cref="PurgeNotBefore"/>
/// are present only when the plan is allowed (zero refusals); a refused plan carries null identity
/// and never generates a runbook.
/// </summary>
public sealed record DecomReport(
    bool Allowed,
    IReadOnlyList<IReadOnlyList<string>> Stages,
    IReadOnlyList<RefusalView> Refusals,
    IReadOnlyList<BlockerView> Blockers,
    IReadOnlyList<ImpactView> Impacts,
    string? PlanHash,
    string? WindowId,
    DateTimeOffset? PurgeNotBefore,
    IReadOnlyList<string> Caveats);

/// <summary>One proven reach path (v2 [16]); <see cref="Origin"/> is the ReachOrigin enum name,
/// <see cref="Via"/> the resource that makes the path exist, <see cref="Evidence"/> the positive
/// snapshot facts the claim is built from.</summary>
public sealed record ReachPathView(
    string Origin,
    string Via,
    IReadOnlyList<string> Hops,
    IReadOnlyList<string> Evidence);

/// <summary>One reach diagnostic; <see cref="Severity"/> is the ArmDiagnosticSeverity enum name.</summary>
public sealed record ReachDiagnosticView(string Severity, string Code, string Message);

/// <summary>
/// Reachability answer over one snapshot (v2 [93]/[16]). <see cref="InternetReachable"/> is true
/// only while a path is PROVEN - a false is not proof of closure; the caveats carry what the
/// snapshot never answers (publicNetworkAccess flags, NSG deny precedence, UDRs/firewall/WAF,
/// RBAC without supplied assignments), and a target outside the scan answers with a diagnostic
/// instead of a verdict.
/// </summary>
public sealed record ReachReport(
    string TargetId,
    bool InternetReachable,
    IReadOnlyList<ReachPathView> Paths,
    IReadOnlyList<string> Principals,
    IReadOnlyList<string> Caveats,
    IReadOnlyList<ReachDiagnosticView> Diagnostics);
