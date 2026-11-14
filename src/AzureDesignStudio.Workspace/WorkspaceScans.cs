using Microsoft.EntityFrameworkCore;

namespace AzureDesignStudio.Workspace;

/// <summary>Storage-neutral snapshot row; the host maps Discovery items onto it.</summary>
public sealed record InventorySnapshotRow(
    string Id, string Name, string ResourceType, string? Location,
    string? ResourceGroup, string? SubscriptionId, string? TagsJson, string? PropertiesJson);

/// <summary>Storage-neutral governance finding row (Wave B3); the host maps Discovery's
/// findings onto it, enum values by name.</summary>
public sealed record GovernanceFindingRow(
    string Source, string RecordId, string ResourceId, string SubscriptionId, string Severity,
    string Category, string Title, string? Detail, string? PolicyAssignmentId, string? PolicyDefinitionId);

/// <summary>
/// Scan persistence (plan 7.5): one ScanRecord per run with manifest + durable checkpoint;
/// snapshots per page so a killed scan keeps its partial inventory and a resumed scan skips
/// finished query groups.
/// </summary>
public sealed class WorkspaceScans(WorkspaceDb db)
{
    /// <summary>A scan interrupted by a kill/crash: Running row with a checkpoint to resume from.</summary>
    public async Task<ScanRecord?> GetResumableAsync()
    {
        await using var ctx = db.CreateContext();
        // SQLite cannot ORDER BY DateTimeOffset; scan history is small, sort in memory.
        var running = await ctx.Scans.Where(s => s.Status == "Running" && s.CheckpointJson != null).ToListAsync();
        return running.OrderByDescending(s => s.StartedAt).FirstOrDefault();
    }

    /// <summary>
    /// Opens a run; pass the existing id when resuming so history stays one row. A fresh
    /// start retires leftover Running rows (crash before first page) - they can only be
    /// picked up through the resume path, never silently shadowed.
    /// </summary>
    public async Task<ScanRecord> StartAsync(string scope, string scopeType, string? resumeScanId = null, string? checkpointJson = null)
    {
        await using var ctx = db.CreateContext();
        ScanRecord scan;
        if (resumeScanId is null)
        {
            var stale = await ctx.Scans.Where(s => s.Status == "Running").ToListAsync();
            foreach (var old in stale)
            {
                old.Status = "Interrupted";
                old.FinishedAt = DateTimeOffset.UtcNow;
                old.Error ??= "replaced by new scan";
            }
            scan = new ScanRecord { Scope = scope, ScopeType = scopeType, Status = "Running", StartedAt = DateTimeOffset.UtcNow, CheckpointJson = checkpointJson };
            ctx.Scans.Add(scan);
        }
        else scan = await ctx.Scans.SingleAsync(s => s.Id == resumeScanId);
        if (resumeScanId is not null)
        {
            scan.Status = "Running";
            scan.FinishedAt = null;
            scan.Error = null;
            if (checkpointJson is not null) scan.CheckpointJson = checkpointJson;
        }
        await ctx.SaveChangesAsync();
        return scan;
    }

    /// <summary>One collector page: snapshots + checkpoint in a single transaction.</summary>
    public async Task PersistPageAsync(string scanId, IReadOnlyList<InventorySnapshotRow> rows, string checkpointJson)
    {
        await using var ctx = db.CreateContext();
        await using var transaction = await ctx.Database.BeginTransactionAsync();
        var takenAt = DateTimeOffset.UtcNow;

        // A resumed scan re-runs the query group it was killed in, so its pages arrive a
        // second time. Appending them left every such resource in the scan twice: the count
        // was inflated and each reader had to remember to de-duplicate. One row per resource
        // per scan, the later page winning.
        rows = rows.GroupBy(r => r.Id, StringComparer.Ordinal).Select(g => g.Last()).ToList();
        var replaced = 0;
        foreach (var ids in rows.Select(r => r.Id).Chunk(500))
            replaced += await ctx.ResourceSnapshots.Where(s => s.ScanId == scanId && ids.Contains(s.ResourceId)).ExecuteDeleteAsync();

        ctx.ResourceSnapshots.AddRange(rows.Select(r => new ResourceSnapshotRecord
        {
            ScanId = scanId,
            ResourceId = r.Id,
            ResourceType = r.ResourceType,
            Name = r.Name,
            Location = r.Location,
            ResourceGroup = r.ResourceGroup,
            SubscriptionId = r.SubscriptionId,
            TagsJson = r.TagsJson,
            PropertiesJson = r.PropertiesJson,
            TakenAt = takenAt,
        }));
        var scan = await ctx.Scans.SingleAsync(s => s.Id == scanId);
        scan.CheckpointJson = checkpointJson;
        scan.ResourceCount += rows.Count - replaced;
        await ctx.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    /// <summary>
    /// One governance source group (Wave B3): its findings and the checkpoint that names the
    /// group complete, in a single transaction - a page that cannot be stored leaves both as
    /// they were. Findings are upserted on (scan, source, record id), so a group stored a second
    /// time replaces its rows instead of doubling them; a record listed twice in one page is
    /// stored once, the later one winning. <see cref="ScanRecord.ResourceCount"/> counts the
    /// inventory and is not touched.
    /// </summary>
    public async Task PersistSourcePageAsync(string scanId, IReadOnlyList<GovernanceFindingRow> rows, string checkpointJson, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rows);
        await using var ctx = db.CreateContext();
        await using var transaction = await ctx.Database.BeginTransactionAsync(ct);
        var scan = await ctx.Scans.SingleAsync(s => s.Id == scanId, ct);

        // Keys compare ordinally, as SQLite compares the TEXT primary key.
        rows = rows.GroupBy(r => (r.Source, r.RecordId)).Select(g => g.Last()).ToList();
        foreach (var bySource in rows.GroupBy(r => r.Source, StringComparer.Ordinal))
        {
            var source = bySource.Key;
            foreach (var ids in bySource.Select(r => r.RecordId).Chunk(500))
                await ctx.GovernanceFindings
                    .Where(f => f.ScanId == scanId && f.Source == source && ids.Contains(f.RecordId))
                    .ExecuteDeleteAsync(ct);
        }

        ctx.GovernanceFindings.AddRange(rows.Select(r => new GovernanceFindingRecord
        {
            ScanId = scanId,
            Source = r.Source,
            RecordId = r.RecordId,
            ResourceId = r.ResourceId,
            SubscriptionId = r.SubscriptionId,
            Severity = r.Severity,
            Category = r.Category,
            Title = r.Title,
            Detail = r.Detail,
            PolicyAssignmentId = r.PolicyAssignmentId,
            PolicyDefinitionId = r.PolicyDefinitionId,
        }));
        scan.CheckpointJson = checkpointJson;
        await ctx.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    /// <summary>The governance findings stored with one scan (none for a scan that collected
    /// none, or that predates them - its manifest tells the two apart), ordered by resource id
    /// (case-insensitive), then source and record id.</summary>
    public async Task<IReadOnlyList<GovernanceFindingRow>> GetGovernanceAsync(string scanId, CancellationToken ct = default)
    {
        await using var ctx = db.CreateContext();
        var records = await ctx.GovernanceFindings.AsNoTracking().Where(f => f.ScanId == scanId).ToListAsync(ct);
        return records
            .OrderBy(f => f.ResourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Source, StringComparer.Ordinal)
            .ThenBy(f => f.RecordId, StringComparer.Ordinal)
            .Select(f => new GovernanceFindingRow(f.Source, f.RecordId, f.ResourceId, f.SubscriptionId, f.Severity,
                f.Category, f.Title, f.Detail, f.PolicyAssignmentId, f.PolicyDefinitionId))
            .ToList();
    }

    public async Task FinishAsync(string scanId, string status, string checkpointJson, string manifestJson, string? error)
    {
        await using var ctx = db.CreateContext();
        var scan = await ctx.Scans.SingleAsync(s => s.Id == scanId);
        var checkpoint = ScanCheckpointView.Read(checkpointJson);
        scan.Status = status;
        scan.FinishedAt = DateTimeOffset.UtcNow;
        scan.CheckpointJson = checkpointJson;
        scan.ManifestJson = manifestJson;
        scan.ContentHash = checkpoint.Hash;
        scan.ResourceCount = checkpoint.Rows;
        scan.Error = error;
        await ctx.SaveChangesAsync();
    }

    /// <summary>Latest finished scan with its full inventory (browse/trend source).</summary>
    public async Task<ScanRecord?> GetLatestCompletedAsync()
    {
        await using var ctx = db.CreateContext();
        // Pick the scan first, then load only its snapshots. Including them for every
        // completed scan read the whole history's inventory to return one scan.
        // (SQLite cannot ORDER BY DateTimeOffset; the scan rows themselves are few.)
        var completed = await ctx.Scans.AsNoTracking().Where(s => s.Status == "Completed").ToListAsync();
        var latest = completed.OrderByDescending(s => s.FinishedAt).FirstOrDefault();
        if (latest is null)
            return null;

        return await ctx.Scans.Include(s => s.Snapshots).SingleAsync(s => s.Id == latest.Id);
    }

    /// <summary>Scan history (newest first) without snapshot payloads.</summary>
    public async Task<IReadOnlyList<ScanRecord>> ListAsync()
    {
        await using var ctx = db.CreateContext();
        var scans = await ctx.Scans.ToListAsync();
        return scans.OrderByDescending(s => s.StartedAt).ToList();
    }

    /// <summary>All snapshot rows of one scan (browse source), one per resource. Scans stored
    /// before pages were upserted can still hold duplicates from a resume, so readers that
    /// de-duplicate should keep doing so.</summary>
    public async Task<IReadOnlyList<ResourceSnapshotRecord>> GetSnapshotsAsync(string scanId)
    {
        await using var ctx = db.CreateContext();
        return await ctx.ResourceSnapshots
            .Where(s => s.ScanId == scanId)
            .OrderBy(s => s.ResourceType).ThenBy(s => s.Name)
            .ToListAsync();
    }
}

internal readonly record struct ScanCheckpointView(int Rows, string? Hash)
{
    public static ScanCheckpointView Read(string checkpointJson)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(checkpointJson);
        var root = doc.RootElement;
        return new(
            root.TryGetProperty("rows", out var r) ? r.GetInt32() : 0,
            root.TryGetProperty("contentHash", out var h) && h.ValueKind == System.Text.Json.JsonValueKind.String ? h.GetString() : null);
    }
}
