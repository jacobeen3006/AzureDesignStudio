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
        EnsureAddedColumns(db);
        EnsureAdditiveTables();
    }

    private void EnsureAddedColumns(WorkspaceDbContext db)
    {
        var conn = db.Database.GetDbConnection();
        conn.Open();
        foreach (var (table, column) in new[] { ("Scans", "ManifestJson"), ("Scans", "CheckpointJson") })
        {
            using var check = conn.CreateCommand();
            check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name='{column}';";
            if (Convert.ToInt64(check.ExecuteScalar()) == 0)
            {
                using var add = conn.CreateCommand();
                add.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} TEXT;";
                add.ExecuteNonQuery();
            }
        }
        conn.Close();
    }

    // EnsureCreated skips new tables on an existing database; CREATE TABLE IF NOT EXISTS keeps
    // the additive bootstrap idempotent for both fresh and upgraded workspace files.
    private void EnsureAdditiveTables()
    {
        using var db = CreateContext();
        var conn = db.Database.GetDbConnection();
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS "NodePositions" (
                "NodeId" TEXT NOT NULL PRIMARY KEY,
                "ContainerId" TEXT NOT NULL,
                "X" REAL NOT NULL,
                "Y" REAL NOT NULL,
                "SavedAt" TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS "ChangeFeed" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ChangeFeed" PRIMARY KEY AUTOINCREMENT,
                "ScanId" TEXT NULL,
                "ChangeKey" TEXT NOT NULL,
                "ChangeTime" TEXT NOT NULL,
                "TargetResourceId" TEXT NOT NULL,
                "TargetResourceType" TEXT NULL,
                "ChangeType" TEXT NOT NULL,
                "CorrelationId" TEXT NULL,
                "ChangedBy" TEXT NULL,
                "ChangedByType" TEXT NULL,
                "ClientType" TEXT NULL,
                "Operation" TEXT NULL,
                "ChangesCount" INTEGER NOT NULL,
                "ChangedPropertiesJson" TEXT NULL
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ChangeFeed_TargetResourceId_ChangeKey" ON "ChangeFeed" ("TargetResourceId", "ChangeKey");
            CREATE INDEX IF NOT EXISTS "IX_ChangeFeed_ChangeTime" ON "ChangeFeed" ("ChangeTime");
            CREATE INDEX IF NOT EXISTS "IX_ChangeFeed_ScanId" ON "ChangeFeed" ("ScanId");
            CREATE TABLE IF NOT EXISTS "DecomWindows" (
                "WindowId" TEXT NOT NULL PRIMARY KEY,
                "ScanId" TEXT NOT NULL,
                "SubscriptionId" TEXT NOT NULL,
                "PlanHash" TEXT NOT NULL,
                "ExecutionHash" TEXT NOT NULL,
                "ScopesJson" TEXT NOT NULL,
                "State" TEXT NOT NULL,
                "RowsJson" TEXT NULL,
                "UpdatedAtUtc" TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS "IX_DecomWindows_SubscriptionId_WindowId" ON "DecomWindows" ("SubscriptionId", "WindowId");
            """;
        cmd.ExecuteNonQuery();
        conn.Close();
    }

    public WorkspaceDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<WorkspaceDbContext>()
            .UseSqlite(ConnectionString)
            .Options;
        return new WorkspaceDbContext(options);
    }
}
