using AzureDesignStudio.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureDesignStudio.Mcp;

/// <summary>
/// Read-only window over the desktop workspace database: per-call contexts on a
/// Mode=ReadOnly shared-cache connection, so the server works while the desktop app
/// holds the file open and can never write. Deliberately not <see cref="WorkspaceDb"/>:
/// its bootstrap path (EnsureCreated / column + table migration) writes, and the class
/// is sealed - a read-only server must not run it.
/// </summary>
public sealed class AdsWorkspace
{
    private readonly string _connectionString;

    public AdsWorkspace(string databasePath)
    {
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    private WorkspaceDbContext NewContext()
    {
        var options = new DbContextOptionsBuilder<WorkspaceDbContext>()
            .UseSqlite(_connectionString)
            .Options;
        return new WorkspaceDbContext(options);
    }

    /// <summary>True when the table exists in the database file. New tables are created by the desktop app's first run after an update; this read-only server must report their absence honestly instead of throwing.</summary>
    private async Task<bool> HasTableAsync(string tableName)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        command.Parameters.AddWithValue("$name", tableName);
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
