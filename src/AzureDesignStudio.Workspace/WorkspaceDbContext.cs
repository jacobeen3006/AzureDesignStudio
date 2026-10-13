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
    }
}

/// <summary>
/// Opens (and on first use creates) the workspace database. One instance lives in the
/// host's DI container; contexts are cheap and created per operation.
/// </summary>
public sealed class WorkspaceDb
{
    public string ConnectionString { get; }

    public WorkspaceDb(string databasePath)
    {
        ConnectionString = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Cache = Microsoft.Data.Sqlite.SqliteCacheMode.Shared,
        }.ToString();

        using var db = CreateContext();
        db.Database.EnsureCreated();
    }

    public WorkspaceDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<WorkspaceDbContext>()
            .UseSqlite(ConnectionString)
            .Options;
        return new WorkspaceDbContext(options);
    }
}
