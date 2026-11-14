using Microsoft.EntityFrameworkCore;

namespace AzureDesignStudio.Workspace;

/// <summary>
/// The desktop workspace database (plan 6.3 / item 90): designs, scans, snapshots,
/// cost cache, plans and audit records in one SQLite file so the app stays fully
/// readable with the network down.
/// </summary>
public class WorkspaceDbContext(DbContextOptions<WorkspaceDbContext> options) : DbContext(options)
{
    public DbSet<DesignRecord> Designs => Set<DesignRecord>();
    public DbSet<ScanRecord> Scans => Set<ScanRecord>();
    public DbSet<ResourceSnapshotRecord> ResourceSnapshots => Set<ResourceSnapshotRecord>();
    public DbSet<CostCacheEntry> CostCache => Set<CostCacheEntry>();
    public DbSet<ExecutionPlanRecord> ExecutionPlans => Set<ExecutionPlanRecord>();
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();
    public DbSet<NodePositionRecord> NodePositions => Set<NodePositionRecord>();
    public DbSet<ChangeFeedRecord> ChangeFeed => Set<ChangeFeedRecord>();
    public DbSet<DecomWindowModel> DecomWindows => Set<DecomWindowModel>();
    public DbSet<GovernanceFindingRecord> GovernanceFindings => Set<GovernanceFindingRecord>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<DesignRecord>(e =>
        {
            e.HasIndex(d => d.Name).IsUnique();
            e.HasIndex(d => d.SyncedAt);
            e.Ignore(d => d.Pending);
        });

        model.Entity<ScanRecord>(e =>
        {
            e.HasIndex(s => new { s.Scope, s.StartedAt });
            e.HasMany(s => s.Snapshots)
             .WithOne(p => p.Scan)
             .HasForeignKey(p => p.ScanId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        model.Entity<ResourceSnapshotRecord>(e =>
        {
            e.HasIndex(p => new { p.ScanId, p.ResourceId });
            e.HasIndex(p => new { p.ResourceId, p.TakenAt });
        });

        model.Entity<CostCacheEntry>(e =>
        {
            e.HasKey(c => c.Key);
            e.HasIndex(c => c.ExpiresAt);
        });

        model.Entity<ExecutionPlanRecord>(e =>
        {
            e.HasIndex(p => p.PlanHash);
            e.HasIndex(p => p.Status);
        });

        model.Entity<AuditRecord>(e => e.HasIndex(a => a.Timestamp));
        model.Entity<NodePositionRecord>(e => e.HasKey(p => p.NodeId));
        model.Entity<ChangeFeedRecord>(e =>
        {
            e.HasIndex(c => new { c.TargetResourceId, c.ChangeKey }).IsUnique();
            e.HasIndex(c => c.ChangeTime);
            e.HasIndex(c => c.ScanId);
        });
        model.Entity<DecomWindowModel>(e =>
        {
            e.HasKey(w => w.WindowId);
            e.HasIndex(w => new { w.SubscriptionId, w.WindowId });
        });
        model.Entity<GovernanceFindingRecord>(e =>
        {
            e.HasKey(g => new { g.ScanId, g.Source, g.RecordId });
            e.HasIndex(g => new { g.ScanId, g.ResourceId });
        });
    }
}

/// <summary>
/// Opens (and on first use creates) the workspace database, bringing its schema current through
/// <see cref="WorkspaceSchema"/>. One instance lives in the host's DI container; contexts are cheap
/// and created per operation.
/// </summary>
public sealed class WorkspaceDb
{
    private readonly string _databasePath;

    public string ConnectionString { get; }

    /// <summary>The workspace database file this instance opens.</summary>
    public string DatabasePath => _databasePath;

    public WorkspaceDb(string databasePath)
    {
        _databasePath = databasePath;
        ConnectionString = WorkspaceConnection.WriterConnectionString(databasePath);

        // The thread-pool hop keeps a UI synchronization context from deadlocking the blocking wait.
        Task.Run(() => WorkspaceSchema.UpgradeAsync(databasePath, new WorkspaceUpgradeOptions(), CancellationToken.None))
            .GetAwaiter().GetResult();
    }

    /// <summary>For a file <see cref="WorkspaceStartup"/> has already brought to the current schema: no upgrade runs.</summary>
    internal WorkspaceDb(string databasePath, bool alreadyUpgraded)
    {
        _ = alreadyUpgraded;
        _databasePath = databasePath;
        ConnectionString = WorkspaceConnection.WriterConnectionString(databasePath);
    }

    public WorkspaceDbContext CreateContext()
    {
        // The factory attaches the pragma interceptor, so a pooled connection carries busy_timeout and foreign_keys.
        return new WorkspaceDbContext(WorkspaceConnection.WriterOptions(_databasePath));
    }
}
