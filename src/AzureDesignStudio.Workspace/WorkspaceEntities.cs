namespace AzureDesignStudio.Workspace;

/// <summary>Design saved locally while the server was unreachable (pending until synced).</summary>
public class DesignRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string Name { get; set; }
    public required string Data { get; set; }
    public DateTimeOffset SavedAt { get; set; }
    /// <summary>Null while the design is waiting for its next server sync.</summary>
    public DateTimeOffset? SyncedAt { get; set; }
    public bool Pending => SyncedAt == null;
}

/// <summary>One tenant/subscription scan run (Phase 7 discovery writes these).</summary>
public class ScanRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    /// <summary>Management scope scanned: tenant, MG, subscription or RG id.</summary>
    public required string Scope { get; set; }
    public required string ScopeType { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>Hash over the result set; identical hashes mark an unchanged inventory.</summary>
    public string? ContentHash { get; set; }
    public int ResourceCount { get; set; }
    public string? Error { get; set; }
    /// <summary>Scan manifest: scope, group counts, partial failures (plan 7.5).</summary>
    public string? ManifestJson { get; set; }
    /// <summary>Durable resume state; a killed scan continues from here (plan 7.5).</summary>
    public string? CheckpointJson { get; set; }
    public List<ResourceSnapshotRecord> Snapshots { get; set; } = [];
}

/// <summary>Point-in-time row of the inventory; scan history becomes trend data.</summary>
public class ResourceSnapshotRecord
{
    public long Id { get; set; }
    public ScanRecord? Scan { get; set; }
    public required string ResourceId { get; set; }
    public required string ResourceType { get; set; }
    public string ScanId { get; set; } = null!; // set by EF from the Scan navigation
    public required string Name { get; set; }
    public string? Location { get; set; }
    public string? ResourceGroup { get; set; }
    public string? SubscriptionId { get; set; }
    public string? TagsJson { get; set; }
    public string? PropertiesJson { get; set; }
    public DateTimeOffset TakenAt { get; set; }
}

/// <summary>Cached Azure Retail Prices API response (works offline / rate-limit friendly).</summary>
public class CostCacheEntry
{
    /// <summary>Meter key: itemId|region|skuName (case-insensitive by convention).</summary>
    public required string Key { get; set; }
    public required string Json { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>Node placement keyed by ARM resource id: a rescan finds the same id and keeps
/// the position (plan 8.2 - identity-stable layout). Coordinates are local to the frame
/// container recorded in ContainerId (e.g. "rg:sub|myrg" or a parent resource id).</summary>
public class NodePositionRecord
{
    public required string NodeId { get; set; }
    public required string ContainerId { get; set; }
    public double X { get; set; }
    public double Y { get; set; }
    public DateTimeOffset SavedAt { get; set; }
}

/// <summary>Executable change plan (Phase 9 guardrails persist these before anything runs).</summary>
public class ExecutionPlanRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public required string DesignName { get; set; }
    public required string Json { get; set; }
    /// <summary>Hash of the plan body; audit records and previews bind to it.</summary>
    public required string PlanHash { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTimeOffset? ApprovedAt { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }
}

/// <summary>Append-only trace of destructive/privileged operations.</summary>
public class AuditRecord
{
    public long Id { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public required string Target { get; set; }
    public string? PlanHash { get; set; }
    public string? DetailJson { get; set; }
}

/// <summary>
/// One control-plane change from the ARG change feed (plan 9.1), persisted so drift
/// (9.2) can diff scan↔now offline. Attribution fields keep ARG placeholders as null;
/// "unknown actor" is a UI rendering of null.
/// </summary>
public class ChangeFeedRecord
{
    public long Id { get; set; }
    /// <summary>Scan run that collected the entry; null for ad-hoc collection.</summary>
    public string? ScanId { get; set; }
    public required string ChangeKey { get; set; }
    public DateTimeOffset ChangeTime { get; set; }
    public required string TargetResourceId { get; set; }
    public string? TargetResourceType { get; set; }
    public required string ChangeType { get; set; }
    public string? CorrelationId { get; set; }
    public string? ChangedBy { get; set; }
    public string? ChangedByType { get; set; }
    public string? ClientType { get; set; }
    public string? Operation { get; set; }
    public int ChangesCount { get; set; }
    public string? ChangedPropertiesJson { get; set; }
}

/// <summary>
/// v2 13.5: one decommission window's execution state, persisted so the release verb
/// re-sends the stored scopes (selection drift-proof) and an offline reopen shows the
/// last known state. State: executed (all locked|already-locked), partial (some failed),
/// released, failed.
/// </summary>
public class DecomWindowModel
{
    public required string WindowId { get; set; }
    public required string ScanId { get; set; }
    public required string SubscriptionId { get; set; }
    public required string PlanHash { get; set; }
    public required string ExecutionHash { get; set; }
    /// <summary>JSON array of the window-owned scopes; release re-sends exactly these.</summary>
    public required string ScopesJson { get; set; }
    /// <summary>executed | partial | released | failed.</summary>
    public required string State { get; set; }
    /// <summary>Latest per-scope outcomes (DecomLockRow shape) as JSON.</summary>
    public string? RowsJson { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
