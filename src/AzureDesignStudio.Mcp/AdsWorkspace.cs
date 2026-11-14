using AzureDesignStudio.Discovery.Governance;
using AzureDesignStudio.Workspace;
using Microsoft.EntityFrameworkCore;

namespace AzureDesignStudio.Mcp;

/// <summary>
/// Read-only window over the desktop workspace database: per-call contexts on a connection built by
/// <see cref="WorkspaceConnection.OpenReader"/>, so the server works while the desktop app holds the
/// file open and can never write (read-only open; if the platform refuses that, read-write with
/// <c>query_only</c>; in a read-only folder with no live log, <c>immutable=1</c>). The rung that
/// opened it is exposed as <see cref="OpenMode"/> and reported on stderr by the host. Deliberately
/// not <see cref="WorkspaceDb"/>: its schema upgrade writes, and a read-only server must never run it.
/// </summary>
public sealed class AdsWorkspace
{
    private readonly WorkspaceReaderOpen _open;

    public AdsWorkspace(string databasePath)
    {
        _open = WorkspaceConnection.OpenReader(databasePath);
    }

    /// <summary>The rung of the reader ladder that opened the workspace.</summary>
    public WorkspaceOpenMode OpenMode => _open.Mode;

    /// <summary>One sentence saying how the workspace was opened and why that rung.</summary>
    public string OpenDetail => _open.Detail;

    private WorkspaceDbContext NewContext() => new(WorkspaceConnection.ReaderOptions(_open));

    /// <summary>True when the table exists in the database file. New tables are created by the desktop app's first run after an update; this read-only server must report their absence honestly instead of throwing.</summary>
    private async Task<bool> HasTableAsync(string tableName)
    {
        await using var ctx = NewContext();
        await ctx.Database.OpenConnectionAsync();   // opens through EF so the pragma interceptor runs
        await using var command = ctx.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        var name = command.CreateParameter();
        name.ParameterName = "$name";
        name.Value = tableName;
        command.Parameters.Add(name);
        return await command.ExecuteScalarAsync() is not null;
    }

    /// <summary>Scan history, newest first (SQLite cannot ORDER BY DateTimeOffset - client-side, per the workspace convention).</summary>
    public async Task<IReadOnlyList<ScanRecord>> ListScansAsync()
    {
        await using var ctx = NewContext();
        var rows = await ctx.Scans.AsNoTracking().ToListAsync();
        return rows.OrderByDescending(s => s.StartedAt).ToList();
    }

    public async Task<ScanRecord?> GetScanAsync(string scanId)
    {
        await using var ctx = NewContext();
        return await ctx.Scans.AsNoTracking().FirstOrDefaultAsync(s => s.Id == scanId);
    }

    /// <summary>All snapshot rows of one scan; a resumed scan may hold duplicate resource rows - readers deduplicate.</summary>
    public async Task<IReadOnlyList<ResourceSnapshotRecord>> GetSnapshotsAsync(string scanId)
    {
        await using var ctx = NewContext();
        return await ctx.ResourceSnapshots.AsNoTracking().Where(r => r.ScanId == scanId).ToListAsync();
    }

    public async Task<IReadOnlyList<DesignRecord>> ListDesignsAsync()
    {
        await using var ctx = NewContext();
        var rows = await ctx.Designs.AsNoTracking().ToListAsync();
        return rows.OrderByDescending(d => d.SavedAt).ToList();
    }

    public async Task<DesignRecord?> GetDesignAsync(string designId)
    {
        await using var ctx = NewContext();
        return await ctx.Designs.AsNoTracking().FirstOrDefaultAsync(d => d.Id == designId);
    }

    public async Task<IReadOnlyList<CostCacheEntry>> ListCostCacheAsync()
    {
        if (!await HasTableAsync(nameof(WorkspaceDbContext.CostCache))) return [];
        await using var ctx = NewContext();
        return await ctx.CostCache.AsNoTracking().ToListAsync();
    }

    /// <summary>The whole change feed; it is window-bounded and small, filtered/sorted in memory (same as the workspace API).</summary>
    public async Task<IReadOnlyList<ChangeFeedRecord>> ListChangeFeedAsync()
    {
        if (!await HasTableAsync(nameof(WorkspaceDbContext.ChangeFeed))) return [];
        await using var ctx = NewContext();
        return await ctx.ChangeFeed.AsNoTracking().ToListAsync();
    }

    /// <summary>
    /// The governance findings stored with one scan (Wave B3). A database the desktop app has not
    /// upgraded yet has no GovernanceFindings table: that answers empty with
    /// <see cref="StoredGovernance.TableMissing"/> set, never an exception. A row whose source or
    /// severity is not a known name is skipped and counted in <see cref="StoredGovernance.Malformed"/>
    /// (<see cref="GovernanceNormalizer.FromStored"/>, the mapping the desktop drawer uses too).
    /// </summary>
    public async Task<StoredGovernance> ListGovernanceAsync(string scanId)
    {
        if (!await HasTableAsync(nameof(WorkspaceDbContext.GovernanceFindings)))
            return new StoredGovernance([], 0, TableMissing: true);
        await using var ctx = NewContext();
        var records = await ctx.GovernanceFindings.AsNoTracking().Where(f => f.ScanId == scanId).ToListAsync();

        var findings = new List<GovernanceFinding>(records.Count);
        var malformed = 0;
        foreach (var r in records)
        {
            if (GovernanceNormalizer.FromStored(r.Source, r.RecordId, r.ResourceId, r.SubscriptionId, r.Severity,
                    r.Category, r.Title, r.Detail, r.PolicyAssignmentId, r.PolicyDefinitionId) is { } finding)
                findings.Add(finding);
            else
                malformed++;
        }
        return new StoredGovernance(findings, malformed, TableMissing: false);
    }

    public async Task<DecomWindowModel?> GetDecomWindowAsync(string subscriptionId, string windowId)
    {
        if (!await HasTableAsync(nameof(WorkspaceDbContext.DecomWindows))) return null;
        await using var ctx = NewContext();
        return await ctx.DecomWindows.AsNoTracking().FirstOrDefaultAsync(w =>
            w.SubscriptionId == subscriptionId && w.WindowId == windowId);
    }

    public async Task<DecomWindowModel?> LatestDecomWindowAsync()
    {
        if (!await HasTableAsync(nameof(WorkspaceDbContext.DecomWindows))) return null;
        await using var ctx = NewContext();
        var rows = await ctx.DecomWindows.AsNoTracking().ToListAsync();
        return rows.OrderByDescending(w => w.UpdatedAtUtc).FirstOrDefault();
    }
}

/// <summary>One scan's readable governance findings, how many stored rows could not be read,
/// and whether the database has no governance table yet.</summary>
public sealed record StoredGovernance(IReadOnlyList<GovernanceFinding> Findings, int Malformed, bool TableMissing);
