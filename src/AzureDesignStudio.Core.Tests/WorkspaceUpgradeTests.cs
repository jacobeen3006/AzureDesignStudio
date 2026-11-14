using System;
using System.Linq;
using System.Threading.Tasks;
using AzureDesignStudio.Core.Tests.Support;
using AzureDesignStudio.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// A legacy workspace.db (no migration history) upgrades through the guarded-SQL baseline
    /// reached by <c>new WorkspaceDb(path)</c>: rows kept, one history row, the schema of a fresh file.
    /// Fixtures load into temp files only.
    /// </summary>
    public class WorkspaceUpgradeTests : IDisposable
    {
        private readonly TempWorkspaceDirectory _temp = new();

        public void Dispose() => _temp.Dispose();

        [Fact]
        public void LegacyL3_UpgradesKeepingEveryRow_AndMatchesAFreshWorkspace()
        {
            var path = _temp.Path("legacy.db");
            WorkspaceFixtures.Load(WorkspaceFixtures.L3CurrentLegacy, path);
            var before = WorkspaceFixtures.TableCounts(path);
            Assert.Equal("delete", JournalMode(path));

            _ = new WorkspaceDb(path);
            SqliteConnection.ClearAllPools();

            Assert.Equal(before, WorkspaceFixtures.TableCounts(path));
            Assert.Equal(1, WorkspaceFixtures.Scalar(path, "SELECT COUNT(*) FROM \"__EFMigrationsHistory\""));
            Assert.EndsWith("_Baseline",
                Text(path, "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\""));

            var fresh = _temp.Path("fresh.db");
            _ = new WorkspaceDb(fresh);
            SqliteConnection.ClearAllPools();

            Assert.Equal(SqliteSchemaDescriber.Describe(fresh), SqliteSchemaDescriber.Describe(path));
            Assert.Equal("wal", JournalMode(path));
        }

        private const string Sub = "11111111-1111-1111-1111-111111111111";

        private string Upgraded(string fixtureName, string fileName)
        {
            var path = _temp.Path(fileName);
            WorkspaceFixtures.Load(fixtureName, path);
            _ = new WorkspaceDb(path);
            SqliteConnection.ClearAllPools();
            return path;
        }

        private string Fresh(string fileName = "fresh.db")
        {
            var path = _temp.Path(fileName);
            _ = new WorkspaceDb(path);
            SqliteConnection.ClearAllPools();
            return path;
        }

        [Fact]
        public void LegacyL1_GainsTheScanColumnsAndTheAdditiveTables_KeepingItsRows()
        {
            var path = _temp.Path("l1.db");
            WorkspaceFixtures.Load(WorkspaceFixtures.L1Oldest, path);
            var before = WorkspaceFixtures.TableCounts(path);
            Assert.DoesNotContain("ManifestJson", SqliteSchemaDescriber.ColumnNames(path, "Scans"));

            _ = new WorkspaceDb(path);
            SqliteConnection.ClearAllPools();

            var after = WorkspaceFixtures.TableCounts(path);
            foreach (var (table, count) in before) Assert.Equal(count, after[table]);
            Assert.Contains("ManifestJson", SqliteSchemaDescriber.ColumnNames(path, "Scans"));
            Assert.Contains("CheckpointJson", SqliteSchemaDescriber.ColumnNames(path, "Scans"));
            foreach (var added in new[] { "ChangeFeed", "DecomWindows", "GovernanceFindings", "NodePositions" })
            {
                Assert.True(after.ContainsKey(added), added + " is missing");
                Assert.Equal(0, after[added]);
            }
            Assert.Equal(1, WorkspaceFixtures.Scalar(path, "SELECT COUNT(*) FROM \"__EFMigrationsHistory\""));
            Assert.Equal(SqliteSchemaDescriber.Describe(Fresh()), SqliteSchemaDescriber.Describe(path));
        }

        [Fact]
        public async Task LegacyL2_GainsTheGovernanceTable_AndTakesFindings()
        {
            var path = _temp.Path("l2.db");
            WorkspaceFixtures.Load(WorkspaceFixtures.L2PreGovernance, path);
            var before = WorkspaceFixtures.TableCounts(path);
            Assert.DoesNotContain("GovernanceFindings", before.Keys);

            var workspace = new WorkspaceDb(path);
            SqliteConnection.ClearAllPools();

            var fresh = Fresh();
            Assert.Equal(SqliteSchemaDescriber.DescribeTable(fresh, "GovernanceFindings"),
                SqliteSchemaDescriber.DescribeTable(path, "GovernanceFindings"));
            Assert.Contains("IX_GovernanceFindings_ScanId_ResourceId(ScanId,ResourceId)",
                SqliteSchemaDescriber.CreatedIndexes(path, "GovernanceFindings"));
            Assert.Equal(SqliteSchemaDescriber.Describe(fresh), SqliteSchemaDescriber.Describe(path));

            var after = WorkspaceFixtures.TableCounts(path);
            foreach (var (table, count) in before) Assert.Equal(count, after[table]);
            Assert.Equal(0, after["GovernanceFindings"]);

            // The upgraded file takes findings for a scan that was already there.
            var scans = new WorkspaceScans(workspace);
            await scans.PersistSourcePageAsync("s-1",
                [new GovernanceFindingRow("Advisor", "rec-1", "/subscriptions/" + Sub + "/resourceGroups/rg-fx", Sub,
                    "Medium", "Security", "Enable soft delete", null, null, null)],
                """{"completedQueries":["advisor:g"],"rows":1}""");
            Assert.Equal("rec-1", Assert.Single(await scans.GetGovernanceAsync("s-1")).RecordId);
            Assert.Equal(2, (await scans.ListAsync()).Count);
        }

        [Fact]
        public void ModelBuilt_FileAdoptsTheBaseline_WithoutLosingARow()
        {
            var path = _temp.Path("model.db");
            var options = new DbContextOptionsBuilder<WorkspaceDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder { DataSource = path }.ToString())
                .Options;
            using (var db = new WorkspaceDbContext(options))
            {
                db.Database.EnsureCreated(); // test-only: production no longer creates the schema this way
                db.Designs.Add(new DesignRecord { Name = "kept", Data = "{}", SavedAt = DateTimeOffset.UtcNow });
                db.SaveChanges();
            }
            SqliteConnection.ClearAllPools();
            Assert.Equal(0, WorkspaceFixtures.Scalar(path,
                "SELECT COUNT(*) FROM pragma_table_info('__EFMigrationsHistory')"));

            _ = new WorkspaceDb(path);
            SqliteConnection.ClearAllPools();

            Assert.Equal(1, WorkspaceFixtures.Scalar(path, "SELECT COUNT(*) FROM \"__EFMigrationsHistory\""));
            Assert.Equal(1, WorkspaceFixtures.Scalar(path, "SELECT COUNT(*) FROM \"Designs\" WHERE \"Name\" = 'kept'"));
        }

        [Fact]
        public void Fresh_WorkspaceHasTheSchemaTheModelCreates()
        {
            var modelBuilt = _temp.Path("model-only.db");
            var options = new DbContextOptionsBuilder<WorkspaceDbContext>()
                .UseSqlite(new SqliteConnectionStringBuilder { DataSource = modelBuilt }.ToString())
                .Options;
            using (var db = new WorkspaceDbContext(options)) db.Database.EnsureCreated(); // test-only
            SqliteConnection.ClearAllPools();

            Assert.Equal(SqliteSchemaDescriber.Describe(modelBuilt), SqliteSchemaDescriber.Describe(Fresh()));
        }

        [Fact]
        public void Reopen_AnUpgradedWorkspaceChangesNothing()
        {
            var path = Upgraded(WorkspaceFixtures.L3CurrentLegacy, "reopen.db");
            var counts = WorkspaceFixtures.TableCounts(path);
            var schema = SqliteSchemaDescriber.Describe(path);
            var migrationId = Text(path, "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\"");

            _ = new WorkspaceDb(path);
            SqliteConnection.ClearAllPools();

            Assert.Equal(counts, WorkspaceFixtures.TableCounts(path));
            Assert.Equal(schema, SqliteSchemaDescriber.Describe(path));
            Assert.Equal(1, WorkspaceFixtures.Scalar(path, "SELECT COUNT(*) FROM \"__EFMigrationsHistory\""));
            Assert.Equal(migrationId, Text(path, "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\""));
        }

        [Fact]
        public async Task Instants_OfALegacyFileReadBackUnshifted()
        {
            var path = _temp.Path("instants.db");
            WorkspaceFixtures.Load(WorkspaceFixtures.L3CurrentLegacy, path);
            var workspace = new WorkspaceDb(path);

            await using var db = workspace.CreateContext();
            var scan = await db.Scans.SingleAsync(s => s.Id == "s-1");
            var change = (await db.ChangeFeed.ToListAsync()).Single(c => c.ChangeKey == "key-1");
            var window = await db.DecomWindows.SingleAsync(w => w.WindowId == "w-1");

            var seed = TimeSpan.FromTicks(1234567);
            Assert.Equal(new DateTimeOffset(2026, 3, 10, 10, 11, 1, TimeSpan.Zero) + seed, scan.StartedAt);
            Assert.Equal(new DateTimeOffset(2026, 3, 18, 10, 11, 1, TimeSpan.Zero) + seed, change.ChangeTime);
            Assert.Equal(new DateTimeOffset(2026, 3, 21, 10, 11, 1, TimeSpan.Zero) + seed, window.UpdatedAtUtc);
        }

        private static string JournalMode(string path) => Text(path, "PRAGMA journal_mode");

        private static string Text(string path, string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToString(command.ExecuteScalar()) ?? string.Empty;
        }
    }
}
