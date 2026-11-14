using System.Security.Cryptography;
using AzureDesignStudio.Core.Tests.Support;
using AzureDesignStudio.Desktop;
using AzureDesignStudio.Server.Persistence;
using AzureDesignStudio.Services.Models;
using AzureDesignStudio.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace AzureDesignStudio.Server.Tests.Persistence;

/// <summary>
/// The SQLite migration set of the desktop <c>deploy.db</c>, on real files in a temp directory.
/// Test code may call <c>Migrate()</c> and the model-creation method; the source guards scan only
/// the production projects.
/// </summary>
public sealed class DeployDbMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ads-deploydb-" + Guid.NewGuid().ToString("N"));

    public DeployDbMigrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string NewPath(string name = "deploy.db") => Path.Combine(_dir, name);

    private static string ConnectionString(string path) =>
        new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString();

    /// <summary>The file created from today's model. Only the parity comparison uses it: since the
    /// TenantGuardrails migration the model is no longer the shape any shipped build produced.</summary>
    private static void CreateFromModel(string path)
    {
        using var db = new DesignDbContext(
            new DbContextOptionsBuilder<DesignDbContext>().UseSqlite(ConnectionString(path)).Options);
        db.Database.EnsureCreated();
    }

    /// <summary>
    /// The shape a build before the tenant column produced: the SQLite set migrated to the Baseline
    /// only. With <paramref name="keepHistory"/> false the history table is dropped afterwards, which
    /// emulates a file a build before migrations existed created.
    /// </summary>
    private static void CreateBaselineEraFile(string path, bool keepHistory)
    {
        using (var db = new SqliteDesignDbContext(
                   new DbContextOptionsBuilder<SqliteDesignDbContext>().UseSqlite(ConnectionString(path)).Options))
        {
            var baseline = db.Database.GetMigrations().First();
            Assert.EndsWith("_Baseline", baseline);
            db.GetService<IMigrator>().Migrate(baseline);
        }

        if (!keepHistory)
        {
            SqliteConnection.ClearAllPools();
            Exec(path, "DROP TABLE __EFMigrationsHistory;");
        }
    }

    /// <summary>How many migrations the SQLite set holds; history rows are compared with this, never a literal.</summary>
    private static int MigrationCount()
    {
        using var db = new SqliteDesignDbContext(
            new DbContextOptionsBuilder<SqliteDesignDbContext>().UseSqlite("Data Source=:memory:").Options);
        return db.Database.GetMigrations().Count();
    }

    private static void MigrateWithSqliteSet(string path)
    {
        using var db = new SqliteDesignDbContext(
            new DbContextOptionsBuilder<SqliteDesignDbContext>().UseSqlite(ConnectionString(path)).Options);
        db.Database.Migrate();
    }

    private static long Scalar(string path, string sql)
    {
        using var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Fact]
    public void FreshFile_MigratesAndMatchesAModelCreatedFile()
    {
        var migrated = NewPath("migrated.db");
        var modelBuilt = NewPath("model.db");

        MigrateWithSqliteSet(migrated);
        CreateFromModel(modelBuilt);

        // SQLite can only append a column, so the tenant column sits last in the migrated file and in
        // model order in the model-built one; the logical schema is what must agree.
        Assert.Equal(DescribeIgnoringColumnOrder(modelBuilt), DescribeIgnoringColumnOrder(migrated));
        Assert.Equal(9, SqliteSchemaDescriber.TableNames(migrated).Count);
    }

    [Fact]
    public void FileFromABuildBeforeMigrations_AdoptsTheMigrationSet()
    {
        var path = NewPath();
        CreateBaselineEraFile(path, keepHistory: false);
        using (var connection = new SqliteConnection(ConnectionString(path)))
        {
            connection.Open();
            using var insert = connection.CreateCommand();
            insert.CommandText =
                "INSERT INTO AdsDesigns (Name, UserId, DesignData, LastModified) " +
                "VALUES ('keep me', '11111111-1111-1111-1111-111111111111', '{}', '2026-01-01 00:00:00')";
            insert.ExecuteNonQuery();
        }

        MigrateWithSqliteSet(path);

        Assert.Equal(MigrationCount(), Scalar(path, "SELECT COUNT(*) FROM __EFMigrationsHistory"));
        Assert.EndsWith("_Baseline", ScalarText(path, "SELECT MigrationId FROM __EFMigrationsHistory ORDER BY MigrationId LIMIT 1"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM AdsDesigns WHERE Name = 'keep me'"));
    }

    [Fact]
    public void PendingModelChanges_AreNone()
    {
        using var db = new SqliteDesignDbContextFactory().CreateDbContext([]);

        // The SQLite snapshot and the model agree: a model edit without a new migration fails here.
        Assert.False(db.Database.HasPendingModelChanges());
    }

    // ---- Task 2: DeployDbStartup -------------------------------------------------------------

    private static readonly DateTimeOffset T0 = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static void Exec(string path, string sql)
    {
        using var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void StartupMigrate(string path, string backups, DateTimeOffset now)
    {
        using var db = new SqliteDesignDbContext(
            new DbContextOptionsBuilder<SqliteDesignDbContext>().UseSqlite(ConnectionString(path)).Options);
        DeployDbStartup.Migrate(db, path, backups, new FixedClock(now));
    }

    /// <summary>A model-built file that lost what the oldest releases lacked, one row in each surviving table.</summary>
    private void BuildOldFile(string path)
    {
        CreateBaselineEraFile(path, keepHistory: false);
        Exec(path, """
            INSERT INTO AdsDesigns (Name, UserId, DesignData, LastModified)
                VALUES ('keep me', '11111111-1111-1111-1111-111111111111', '{}', '2026-01-01 00:00:00');
            INSERT INTO AzureSubscriptions (UserId, SubscriptionId, SubscriptionName, TenantId, ClientId, ClientSecret)
                VALUES ('11111111-1111-1111-1111-111111111111', '22222222-2222-2222-2222-222222222222', 'sub',
                        '33333333-3333-3333-3333-333333333333', '44444444-4444-4444-4444-444444444444', 'secret');
            INSERT INTO AdsPlanApprovals (PlanHash, PlanJson, Status, ApprovedBy, ApprovedByUserId, ApprovedAtUtc)
                VALUES ('sha256:0000000000000000000000000000000000000000000000000000000000000000', '{}', 'Approved',
                        'me', '11111111-1111-1111-1111-111111111111', '2026-01-01 00:00:00');
            INSERT INTO AdsPlanApplyEvents (ApplyId, PlanHash, PlanJson, SubscriptionId, ResourceGroupName,
                    DeploymentName, ApprovedBy, ApprovedByUserId, ApprovedAtUtc, EventType, TimestampUtc)
                VALUES ('55555555-5555-5555-5555-555555555555',
                        'sha256:0000000000000000000000000000000000000000000000000000000000000000', '{}', 'sub', 'rg',
                        'dep', 'me', '11111111-1111-1111-1111-111111111111', '2026-01-01 00:00:00', 'Started',
                        '2026-01-01 00:00:00');
            DROP TABLE AdsGuardrailPolicies;
            DROP TABLE AdsDecomApprovals;
            DROP TABLE AdsDecomWindowEvents;
            ALTER TABLE AdsPlanApprovals DROP COLUMN RevertPlanJson;
            ALTER TABLE AdsPlanApplyEvents DROP COLUMN RevertPlanHash;
            ALTER TABLE AdsPlanApplyEvents DROP COLUMN RevertPlanJson;
            ALTER TABLE AdsPlanApplyEvents DROP COLUMN TargetScope;
            """);
    }

    /// <summary>
    /// The logical schema without column positions: SQLite can only append a column, so a file that
    /// gained one later has it last while a fresh table has it in model order.
    /// </summary>
    private static string DescribeIgnoringColumnOrder(string path)
    {
        var result = new List<string>();
        var columns = new List<string>();
        void Flush()
        {
            result.AddRange(columns.OrderBy(l => l, StringComparer.Ordinal));
            columns.Clear();
        }

        foreach (var line in SqliteSchemaDescriber.Describe(path).Split('\n'))
        {
            if (line.TrimStart().StartsWith("COLUMN ", StringComparison.Ordinal))
                columns.Add(System.Text.RegularExpressions.Regex.Replace(line, @"^(\s+COLUMN )\d+\|", "$1"));
            else
            {
                Flush();
                result.Add(line);
            }
        }

        Flush();
        return string.Join('\n', result);
    }

    private static string Sha256(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void OldFile_MissingTablesAndColumns_IsUpgraded()
    {
        var path = NewPath();
        var backups = Path.Combine(_dir, "backups", "deploy");
        BuildOldFile(path);
        var fresh = NewPath("fresh.db");
        MigrateWithSqliteSet(fresh);

        StartupMigrate(path, backups, T0);

        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM AdsDesigns WHERE Name = 'keep me'"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM AzureSubscriptions WHERE ClientSecret = 'secret'"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM AdsPlanApprovals"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM AdsPlanApplyEvents WHERE EventType = 'Started'"));
        Assert.Equal(9, SqliteSchemaDescriber.TableNames(path).Count);
        Assert.Contains("RevertPlanJson", SqliteSchemaDescriber.ColumnNames(path, "AdsPlanApprovals"));
        Assert.Contains("TargetScope", SqliteSchemaDescriber.ColumnNames(path, "AdsPlanApplyEvents"));
        Assert.Equal(DescribeIgnoringColumnOrder(fresh), DescribeIgnoringColumnOrder(path));
        Assert.Equal(MigrationCount(), Scalar(path, "SELECT COUNT(*) FROM __EFMigrationsHistory"));
    }

    [Fact]
    public void PreTenantFile_KeepsItsGuardrailRow_AndGainsTheTenantTables()
    {
        var path = NewPath();
        var backups = Path.Combine(_dir, "backups", "deploy");
        CreateBaselineEraFile(path, keepHistory: true);
        Exec(path, """
            INSERT INTO AdsGuardrailPolicies (Id, Json, UpdatedBy, UpdatedByUserId, UpdatedAtUtc)
                VALUES (1, '{"scopes":["/subscriptions/x"]}', 'operator', '11111111-1111-1111-1111-111111111111',
                        '2026-01-01 00:00:00');
            """);
        Assert.DoesNotContain("TenantId", SqliteSchemaDescriber.ColumnNames(path, "AdsGuardrailPolicies"));
        Assert.Equal(7, SqliteSchemaDescriber.TableNames(path).Count);

        StartupMigrate(path, backups, T0);

        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM AdsGuardrailPolicies"));
        Assert.Equal(1, Scalar(path, "SELECT COUNT(*) FROM AdsGuardrailPolicies WHERE Id = 1 AND TenantId IS NULL AND UpdatedBy = 'operator'"));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM AdsTenantSettings"));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM AdsSecurityEvents"));
        Assert.Equal(9, SqliteSchemaDescriber.TableNames(path).Count);
        Assert.Equal(MigrationCount(), Scalar(path, "SELECT COUNT(*) FROM __EFMigrationsHistory"));
        Assert.Single(WorkspaceBackup.List(backups, "deploy"));
    }

    [Fact]
    public void TheUniqueTenantIndex_AllowsManyNullRowsAndRefusesATenantTwice()
    {
        var path = NewPath();
        MigrateWithSqliteSet(path);
        const string insert = "INSERT INTO AdsGuardrailPolicies (Id, TenantId, Json, UpdatedBy, UpdatedByUserId, UpdatedAtUtc) VALUES ";
        Exec(path, insert + "(1, NULL, '{}', 'a', 'a', '2026-01-01 00:00:00'), (2, NULL, '{}', 'a', 'a', '2026-01-01 00:00:00');");
        Exec(path, insert + "(3, '55555555-5555-5555-5555-555555555555', '{}', 'a', 'a', '2026-01-01 00:00:00');");

        Assert.Throws<SqliteException>(() =>
            Exec(path, insert + "(4, '55555555-5555-5555-5555-555555555555', '{}', 'a', 'a', '2026-01-01 00:00:00');"));
    }

    [Fact]
    public void OldFile_GetsAPrivatePremigrationBackup_ThatTheWorkspaceListNeverSees()
    {
        var path = NewPath();
        var backupsRoot = Path.Combine(_dir, "backups");
        var backups = Path.Combine(backupsRoot, "deploy");
        BuildOldFile(path);

        StartupMigrate(path, backups, T0);

        var deploy = Assert.Single(WorkspaceBackup.List(backups, "deploy"));
        Assert.Equal(BackupReason.Premigration, deploy.Reason);
        Assert.StartsWith("deploy-", Path.GetFileName(deploy.Path), StringComparison.Ordinal);
        var manifest = BackupManifest.Read(BackupManifest.PathFor(deploy.Path));
        Assert.Equal(1, manifest.TableCounts["AdsDesigns"]);
        Assert.Equal(1, manifest.TableCounts["AzureSubscriptions"]);
        Assert.Equal(1, manifest.TableCounts["AdsPlanApprovals"]);
        Assert.Equal(1, manifest.TableCounts["AdsPlanApplyEvents"]);
        Assert.False(manifest.TableCounts.ContainsKey("AdsGuardrailPolicies"));
        Assert.Null(manifest.LatestMigrationId);

        // The user-facing list is the workspace stem over the folder above, and the deploy folder itself.
        Assert.Empty(WorkspaceBackup.List(backupsRoot, "workspace"));
        Assert.Empty(WorkspaceBackup.List(backups, "workspace"));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(backups));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(deploy.Path));
        }
    }

    [Fact]
    public void FailingBackup_AbortsTheUpgradeAndLeavesTheFileUnchanged()
    {
        var path = NewPath();
        BuildOldFile(path);
        var blocker = Path.Combine(_dir, "not-a-folder");
        File.WriteAllText(blocker, "a regular file where the backups folder should be");
        var before = Sha256(path);

        var e = Assert.Throws<InvalidOperationException>(() => StartupMigrate(path, blocker, T0));

        Assert.Contains("not upgraded", e.Message, StringComparison.Ordinal);
        Assert.Equal(before, Sha256(path));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM sqlite_master WHERE name = '__EFMigrationsHistory'"));
        Assert.Equal(0, Scalar(path, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'AdsGuardrailPolicies'"));
        Assert.DoesNotContain("TargetScope", SqliteSchemaDescriber.ColumnNames(path, "AdsPlanApplyEvents"));
    }

    [Fact]
    public void AlreadyMigratedFile_TakesNoFurtherBackup()
    {
        var path = NewPath();
        var backups = Path.Combine(_dir, "backups", "deploy");
        BuildOldFile(path);
        StartupMigrate(path, backups, T0);

        StartupMigrate(path, backups, T0.AddHours(1));

        Assert.Single(WorkspaceBackup.List(backups, "deploy"));
    }

    [Fact]
    public void FreshFile_TakesNoBackup()
    {
        var path = NewPath();
        var backups = Path.Combine(_dir, "backups", "deploy");

        StartupMigrate(path, backups, T0);

        Assert.Equal(9, SqliteSchemaDescriber.TableNames(path).Count);
        Assert.False(Directory.Exists(backups));
    }

    [Fact]
    public async Task OnlyTheNewestThreeDeployBackupsAreKept()
    {
        var path = NewPath();
        var backups = Path.Combine(_dir, "backups", "deploy");
        MigrateWithSqliteSet(path);
        for (var i = 0; i < 4; i++)
            await WorkspaceBackup.CreateAsync(path, backups, BackupReason.Scheduled, T0.AddHours(-10 + i), "deploy");

        StartupMigrate(path, backups, T0);

        var kept = WorkspaceBackup.List(backups, "deploy");
        Assert.Equal(3, kept.Count);
        Assert.Equal(T0.AddHours(-7), kept[0].CreatedUtc);
    }

    private static string ScalarText(string path, string sql)
    {
        using var connection = new SqliteConnection(ConnectionString(path));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (string)command.ExecuteScalar()!;
    }
}
