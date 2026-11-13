using AzureDesignStudio.Discovery.Governance;
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

    /// <summary>
    /// The governance findings stored with one scan (Wave B3). A database the desktop app has not
    /// upgraded yet has no GovernanceFindings table: that answers empty with
    /// <see cref="StoredGovernance.TableMissing"/> set, never an exception. A row whose source or
    /// severity is not a known name is skipped and counted in <see cref="StoredGovernance.Malformed"/>.
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
            if (ByName<GovernanceSource>(r.Source) is not { } source
                || ByName<GovernanceSeverity>(r.Severity) is not { } severity)
            {
                malformed++;
                continue;
            }
            findings.Add(new GovernanceFinding(source, r.RecordId, r.ResourceId, r.SubscriptionId, severity,
                r.Category, r.Title, r.Detail, r.PolicyAssignmentId, r.PolicyDefinitionId));
        }
        return new StoredGovernance(findings, malformed, TableMissing: false);
    }

    /// <summary>The enum member with this name (any case). Matched by name, never Enum.TryParse:
    /// that would also accept "2" or "High, Low".</summary>
    private static T? ByName<T>(string? value) where T : struct, Enum
    {
        var name = value?.Trim();
        foreach (var member in Enum.GetValues<T>())
            if (string.Equals(member.ToString(), name, StringComparison.OrdinalIgnoreCase))
                return member;
        return null;
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
