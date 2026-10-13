using Microsoft.EntityFrameworkCore;

namespace AzureDesignStudio.Workspace;

/// <summary>Storage-neutral snapshot row; the host maps Discovery items onto it.</summary>
public sealed record InventorySnapshotRow(
    string Id, string Name, string ResourceType, string? Location,
    string? ResourceGroup, string? SubscriptionId, string? TagsJson, string? PropertiesJson);

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

    /// <summary>Opens a run; pass the existing id when resuming so history stays one row.</summary>
    public async Task<ScanRecord> StartAsync(string scope, string scopeType, string? resumeScanId = null)
    {
        await using var ctx = db.CreateContext();
        ScanRecord scan;
        if (resumeScanId is null)
        {
            scan = new ScanRecord { Scope = scope, ScopeType = scopeType, Status = "Running", StartedAt = DateTimeOffset.UtcNow };
            ctx.Scans.Add(scan);
        }
        else scan = await ctx.Scans.SingleAsync(s => s.Id == resumeScanId);
        if (resumeScanId is not null)
        {
            scan.Status = "Running";
            scan.FinishedAt = null;
            scan.Error = null;
        }
        await ctx.SaveChangesAsync();
        return scan;
    }

    /// <summary>One collector page: snapshots + checkpoint in a single transaction.</summary>
    public async Task PersistPageAsync(string scanId, IReadOnlyList<InventorySnapshotRow> rows, string checkpointJson)
    {
        await using var ctx = db.CreateContext();
        var takenAt = DateTimeOffset.UtcNow;
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
        scan.ResourceCount += rows.Count;
        await ctx.SaveChangesAsync();
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
        var scans = await ctx.Scans
            .Include(s => s.Snapshots)
            .Where(s => s.Status == "Completed")
            .ToListAsync();
        return scans.OrderByDescending(s => s.FinishedAt).FirstOrDefault();
    }

    /// <summary>Scan history (newest first) without snapshot payloads.</summary>
    public async Task<IReadOnlyList<ScanRecord>> ListAsync()
    {
        await using var ctx = db.CreateContext();
        var scans = await ctx.Scans.ToListAsync();
        return scans.OrderByDescending(s => s.StartedAt).ToList();
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
