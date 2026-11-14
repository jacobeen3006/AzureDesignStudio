using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AzureDesignStudio.Core.Tests.Support;
using AzureDesignStudio.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// The unique-index guard of the first migration: duplicates a unique index would reject are removed
    /// (newest kept) after a backup, with a report of every removed row. Fixtures and scratch files stay in temp.
    /// </summary>
    public class UniqueIndexGuardTests : IDisposable
    {
        private readonly TempWorkspaceDirectory _temp = new();

        public void Dispose() => _temp.Dispose();

        // ---- L4 end to end (tracer) ----

        [Fact]
        public void DuplicatesFixture_IsBackedUpFirst_DedupedKeepingTheNewest_ReportedAndEndsOnTheFreshSchema()
        {
            var path = _temp.Path("l4.db");
            WorkspaceFixtures.Load(WorkspaceFixtures.L4Duplicates, path);
            var otherBefore = WorkspaceFixtures.Scalar(path, "SELECT COUNT(*) FROM \"Scans\"");

            _ = new WorkspaceDb(path);
            SqliteConnection.ClearAllPools();

            var backups = Path.Combine(_temp.Root, "backups");
            var backup = Assert.Single(Directory.GetFiles(backups, "workspace-*-premigration.db"));
            Assert.Equal(1, WorkspaceFixtures.Scalar(backup,
                "SELECT COUNT(*) FROM (SELECT Name FROM Designs GROUP BY Name HAVING COUNT(*) > 1)"));
            Assert.Equal(5, WorkspaceFixtures.Scalar(backup, "SELECT COUNT(*) FROM ChangeFeed"));

            // Designs: the greatest SavedAt survives.
            Assert.Equal(2, WorkspaceFixtures.Scalar(path, "SELECT COUNT(*) FROM Designs WHERE Name = 'dup-design' OR Name = 'unique-design'"));
            Assert.Equal(1, WorkspaceFixtures.Scalar(path, "SELECT COUNT(*) FROM Designs WHERE Id = 'd-dup-2'"));
            Assert.Equal(0, WorkspaceFixtures.Scalar(path, "SELECT COUNT(*) FROM Designs WHERE Id = 'd-dup-1'"));
            Assert.Equal(1, WorkspaceFixtures.Scalar(path, "SELECT COUNT(*) FROM Designs WHERE Id = 'd-unique'"));

            // ChangeFeed: key-1 keeps the later time (rowid 2); key-2 is a tie and keeps the greater rowid (4); key-3 is untouched.
            Assert.Equal(3, WorkspaceFixtures.Scalar(path, "SELECT COUNT(*) FROM ChangeFeed"));
            Assert.Equal(
                new long[] { 2, 4, 5 },
                Rows(path, "SELECT Id FROM ChangeFeed ORDER BY Id").Select(r => (long)r[0]!).ToArray());
            Assert.Equal(otherBefore, WorkspaceFixtures.Scalar(path, "SELECT COUNT(*) FROM \"Scans\""));

            // The report: exactly one parseable line per removed row.
            var report = Assert.Single(Directory.GetFiles(backups, "upgrade-removed-rows-*.jsonl"));
            var lines = File.ReadAllLines(report).Select(l => JsonDocument.Parse(l).RootElement).ToList();
            Assert.Equal(3, lines.Count);
            var design = Assert.Single(lines, l => l.GetProperty("table").GetString() == "Designs");
            Assert.Equal("IX_Designs_Name", design.GetProperty("index").GetString());
            Assert.Equal(new[] { "Name" }, design.GetProperty("keyColumns").EnumerateArray().Select(e => e.GetString()).ToArray());
            Assert.Equal("d-dup-1", design.GetProperty("row").GetProperty("Id").GetString());
            Assert.Equal("{\"nodes\":1}", design.GetProperty("row").GetProperty("Data").GetString());
            Assert.True(design.GetProperty("keptRowId").GetInt64() != design.GetProperty("removedRowId").GetInt64());
            var removedFeed = lines.Where(l => l.GetProperty("table").GetString() == "ChangeFeed").ToList();
            Assert.Equal(new long[] { 1, 3 }, removedFeed.Select(l => l.GetProperty("removedRowId").GetInt64()).OrderBy(x => x).ToArray());
            Assert.Equal(new long[] { 2, 4 }, removedFeed.Select(l => l.GetProperty("keptRowId").GetInt64()).OrderBy(x => x).ToArray());
            Assert.All(removedFeed, l => Assert.Equal("IX_ChangeFeed_TargetResourceId_ChangeKey", l.GetProperty("index").GetString()));

            // Both unique indexes exist and the schema equals a fresh workspace's.
            Assert.Contains("unique IX_Designs_Name(Name)", SqliteSchemaDescriber.CreatedIndexes(path, "Designs"));
            Assert.Contains(SqliteSchemaDescriber.CreatedIndexes(path, "ChangeFeed"),
                i => i.StartsWith("unique IX_ChangeFeed_TargetResourceId_ChangeKey", StringComparison.Ordinal));
            var fresh = _temp.Path("fresh.db");
            _ = new WorkspaceDb(fresh);
            SqliteConnection.ClearAllPools();
            Assert.Equal(SqliteSchemaDescriber.Describe(fresh), SqliteSchemaDescriber.Describe(path));
        }

        [Fact]
        public async Task UpgradeResult_ReportsTheGroupAndRowCounts_AndACleanFileReportsNone()
        {
            var path = _temp.Path("l4-result.db");
            WorkspaceFixtures.Load(WorkspaceFixtures.L4Duplicates, path);

            var result = await WorkspaceSchema.UpgradeAsync(path, new WorkspaceUpgradeOptions(), CancellationToken.None);
            SqliteConnection.ClearAllPools();

            Assert.Equal(2, result.UniqueIndexRepairs.Count);
            var designs = Assert.Single(result.UniqueIndexRepairs, r => r.Table == "Designs");
            Assert.Equal((1, 1), (designs.DuplicateGroups, designs.RowsRemoved));
            var feed = Assert.Single(result.UniqueIndexRepairs, r => r.Table == "ChangeFeed");
            Assert.Equal((2, 2), (feed.DuplicateGroups, feed.RowsRemoved));
            Assert.True(File.Exists(designs.ReportPath));
            Assert.Equal(designs.ReportPath, feed.ReportPath);

            var clean = _temp.Path("l3.db");
            WorkspaceFixtures.Load(WorkspaceFixtures.L3CurrentLegacy, clean);
            var cleanBackups = _temp.Path("backups-clean");
            var cleanResult = await WorkspaceSchema.UpgradeAsync(
                clean, new WorkspaceUpgradeOptions { BackupsDirectory = cleanBackups }, CancellationToken.None);
            SqliteConnection.ClearAllPools();
            Assert.Empty(cleanResult.UniqueIndexRepairs);
            Assert.Empty(Directory.GetFiles(cleanBackups, "upgrade-removed-rows-*"));
        }

        [Fact]
        public async Task TheUpgradeResult_HasALaunchMessageNamingTableCountReportAndBackup_OnlyWhenRowsWereRemoved()
        {
            // WR-06: the host shows this at launch; a removal must not wait for the user to open a drawer
            var path = _temp.Path("l4-message.db");
            WorkspaceFixtures.Load(WorkspaceFixtures.L4Duplicates, path);
            var result = await WorkspaceSchema.UpgradeAsync(path, new WorkspaceUpgradeOptions(), CancellationToken.None);
            SqliteConnection.ClearAllPools();

            var message = result.RemovedRowsMessage;

            Assert.NotNull(message);
            Assert.Contains("Designs: 1 row(s) removed", message, StringComparison.Ordinal);
            Assert.Contains("ChangeFeed: 2 row(s) removed", message, StringComparison.Ordinal);
            Assert.Contains(result.UniqueIndexRepairs[0].ReportPath, message, StringComparison.Ordinal);
            Assert.Contains(result.PremigrationBackupPath!, message, StringComparison.Ordinal);

            var clean = _temp.Path("l3-message.db");
            WorkspaceFixtures.Load(WorkspaceFixtures.L3CurrentLegacy, clean);
            var cleanResult = await WorkspaceSchema.UpgradeAsync(
                clean, new WorkspaceUpgradeOptions { BackupsDirectory = _temp.Path("backups-clean-message") }, CancellationToken.None);
            SqliteConnection.ClearAllPools();
            Assert.Null(cleanResult.RemovedRowsMessage);
        }

        // ---- table-driven behaviors (hand-built tables, not the workspace schema) ----

        private static readonly UniqueIndexSpec ItemsSpec =
            new("Items", "IX_Items_Code", ["Code"], "Stamp DESC, rowid DESC");

        private string BackupFile()
        {
            var path = _temp.Path("backup-" + Guid.NewGuid().ToString("N") + ".db");
            File.WriteAllText(path, "placeholder");
            return path;
        }

        private string NewDb(string sql)
        {
            var path = _temp.Path("t-" + Guid.NewGuid().ToString("N") + ".db");
            using var connection = Open(path);
            Exec(connection, sql);
            return path;
        }

        private static void Exec(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private IReadOnlyList<UniqueIndexRepair> Repair(string db, params UniqueIndexSpec[] specs)
        {
            using var connection = Open(db);
            return UniqueIndexGuard.Repair(connection, specs, _temp.Path("reports"), DateTimeOffset.UtcNow, BackupFile());
        }

        private const string ItemsTable =
            "CREATE TABLE Items (Id INTEGER PRIMARY KEY AUTOINCREMENT, Code TEXT NULL, Part TEXT NULL, Stamp TEXT NOT NULL, Note TEXT NULL);";

        private string ReportsDirectory => _temp.Path("reports");

        [Fact]
        public void KeepNewest_OrdersByTheInstant_NotByTheTextOfTheOffsetTimestamp()
        {
            // WR-05: as text '...01:30:00+02:00' sorts after '...01:10:00+01:00', yet it is the OLDER instant
            // (23:30Z the day before versus 00:10Z)
            var db = NewDb(
                "CREATE TABLE Designs (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, Data TEXT NOT NULL, SavedAt TEXT NOT NULL);" +
                "CREATE TABLE ChangeFeed (Id INTEGER PRIMARY KEY AUTOINCREMENT, TargetResourceId TEXT NOT NULL, ChangeKey TEXT NOT NULL, ChangeTime TEXT NOT NULL);" +
                "INSERT INTO Designs VALUES ('older', 'd', '{}', '2024-10-27 01:30:00.1234567+02:00');" +
                "INSERT INTO Designs VALUES ('newer', 'd', '{}', '2024-10-27 01:10:00.0000000+01:00');" +
                "INSERT INTO ChangeFeed (TargetResourceId, ChangeKey, ChangeTime) VALUES ('r', 'k', '2024-10-27 01:30:00.0000000+02:00');" +
                "INSERT INTO ChangeFeed (TargetResourceId, ChangeKey, ChangeTime) VALUES ('r', 'k', '2024-10-27 01:10:00.0000000+01:00');");

            Assert.Equal(2, Repair(db, [.. UniqueIndexGuard.Specs]).Count);

            Assert.Equal("newer", Rows(db, "SELECT Id FROM Designs").Single()[0]);
            Assert.Equal(2L, Rows(db, "SELECT Id FROM ChangeFeed").Single()[0]);   // the later instant, inserted second
        }

        [Fact]
        public void NoDuplicates_ChangesNothing_WritesNoReport_AndReturnsNoRepair()
        {
            var db = NewDb(ItemsTable +
                "INSERT INTO Items (Code, Stamp) VALUES ('a','1'), ('b','2'), ('c','3');");

            Assert.Empty(Repair(db, ItemsSpec));

            Assert.Equal(3, WorkspaceFixtures.Scalar(db, "SELECT COUNT(*) FROM Items"));
            Assert.False(Directory.Exists(ReportsDirectory) && Directory.GetFiles(ReportsDirectory).Length > 0);
        }

        [Fact]
        public void AnExistingUniqueIndex_SkipsTheSpecWithoutScanning()
        {
            var db = NewDb(ItemsTable + "CREATE UNIQUE INDEX IX_Items_Code ON Items (Code);" +
                "INSERT INTO Items (Code, Stamp) VALUES ('a','1'), ('b','2');");
            // A KeepOrderBy naming a column that does not exist throws when the duplicate query is prepared.
            var tripwire = ItemsSpec with { KeepOrderBy = "no_such_column" };

            Assert.Empty(Repair(db, tripwire));
            Assert.Equal(2, WorkspaceFixtures.Scalar(db, "SELECT COUNT(*) FROM Items"));
        }

        [Fact]
        public void AMissingTable_IsSkippedWithoutError()
        {
            var db = NewDb("CREATE TABLE Other (Id INTEGER PRIMARY KEY);");

            Assert.Empty(Repair(db, ItemsSpec));
        }

        [Fact]
        public void ANonUniqueIndexOfTheSameName_IsDroppedWhenDuplicatesAreRepaired_AndLeftAloneOtherwise()
        {
            var withDuplicates = NewDb(ItemsTable + "CREATE INDEX IX_Items_Code ON Items (Code);" +
                "INSERT INTO Items (Code, Stamp) VALUES ('a','1'), ('a','2');");
            var clean = NewDb(ItemsTable + "CREATE INDEX IX_Items_Code ON Items (Code);" +
                "INSERT INTO Items (Code, Stamp) VALUES ('a','1'), ('b','2');");

            Assert.Single(Repair(withDuplicates, ItemsSpec));
            Assert.Equal(0, WorkspaceFixtures.Scalar(withDuplicates,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_Items_Code'"));

            Assert.Empty(Repair(clean, ItemsSpec));
            Assert.Equal(1, WorkspaceFixtures.Scalar(clean,
                "SELECT COUNT(*) FROM pragma_index_list('Items') WHERE name = 'IX_Items_Code' AND \"unique\" = 0"));
        }

        [Fact]
        public void RowsWithANullKeyColumn_AreNeverDuplicates()
        {
            var db = NewDb(ItemsTable +
                "INSERT INTO Items (Code, Part, Stamp) VALUES (NULL,'x','1'), (NULL,'x','2'), ('k',NULL,'3'), ('k',NULL,'4'), ('k','p','5'), ('k','p','6');");
            var composite = new UniqueIndexSpec("Items", "IX_Items_Code_Part", ["Code", "Part"], "Stamp DESC, rowid DESC");

            var repair = Assert.Single(Repair(db, composite));

            Assert.Equal((1, 1), (repair.DuplicateGroups, repair.RowsRemoved));
            Assert.Equal(5, WorkspaceFixtures.Scalar(db, "SELECT COUNT(*) FROM Items"));
            Assert.Equal(4, WorkspaceFixtures.Scalar(db, "SELECT COUNT(*) FROM Items WHERE Code IS NULL OR Part IS NULL"));
            Assert.Equal(6, WorkspaceFixtures.Scalar(db, "SELECT Id FROM Items WHERE Code = 'k' AND Part = 'p'"));
        }

        [Fact]
        public void EqualOrderingValues_KeepTheGreatestRowId_AndThreeRowsKeepExactlyOne()
        {
            var db = NewDb(ItemsTable +
                "INSERT INTO Items (Code, Stamp) VALUES ('a','same'), ('a','same'), ('a','same'), ('b','1');");

            var repair = Assert.Single(Repair(db, ItemsSpec));

            Assert.Equal((1, 2), (repair.DuplicateGroups, repair.RowsRemoved));
            Assert.Equal(1, WorkspaceFixtures.Scalar(db, "SELECT COUNT(*) FROM Items WHERE Code = 'a'"));
            Assert.Equal(3, WorkspaceFixtures.Scalar(db, "SELECT Id FROM Items WHERE Code = 'a'"));
            Assert.Equal(1, WorkspaceFixtures.Scalar(db, "SELECT COUNT(*) FROM Items WHERE Code = 'b'"));
        }

        [Fact]
        public void AFailureWhileDeleting_RollsBackEveryRow_AndSurfaces()
        {
            var db = NewDb(ItemsTable + "CREATE INDEX IX_Items_Code ON Items (Code);" +
                "INSERT INTO Items (Code, Stamp) VALUES ('a','1'), ('a','2'), ('b','1'), ('b','2');" +
                "CREATE TRIGGER no_delete BEFORE DELETE ON Items WHEN OLD.Code = 'b' BEGIN SELECT RAISE(ABORT, 'delete refused'); END;");

            using (var connection = Open(db))
            {
                var e = Assert.Throws<SqliteException>(() => UniqueIndexGuard.Repair(
                    connection, [ItemsSpec], _temp.Path("reports"), DateTimeOffset.UtcNow, BackupFile()));
                Assert.Contains("delete refused", e.Message, StringComparison.Ordinal);
            }

            Assert.Equal(4, WorkspaceFixtures.Scalar(db, "SELECT COUNT(*) FROM Items"));
            // The dropped index came back with the rollback as well.
            Assert.Equal(1, WorkspaceFixtures.Scalar(db,
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'IX_Items_Code'"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("does-not-exist.db")]
        public void WithoutAnExistingBackup_RepairThrowsAndDeletesNothing(string backup)
        {
            var db = NewDb(ItemsTable + "INSERT INTO Items (Code, Stamp) VALUES ('a','1'), ('a','2');");
            var backupPath = backup.EndsWith(".db", StringComparison.Ordinal) ? _temp.Path(backup) : backup;

            using (var connection = Open(db))
                Assert.Throws<ArgumentException>(() => UniqueIndexGuard.Repair(
                    connection, [ItemsSpec], _temp.Path("reports"), DateTimeOffset.UtcNow, backupPath));

            Assert.Equal(2, WorkspaceFixtures.Scalar(db, "SELECT COUNT(*) FROM Items"));
            Assert.False(Directory.Exists(ReportsDirectory));
        }

        [Fact]
        public void ACustomSpec_IsRepairedByTheSameCodePath()
        {
            var db = NewDb(
                "CREATE TABLE Widgets (Tenant TEXT NOT NULL, Slug TEXT NOT NULL, Rev INTEGER NOT NULL, Body TEXT);" +
                "INSERT INTO Widgets VALUES ('t1','s',1,'old'), ('t1','s',3,'newest'), ('t1','s',2,'mid'), ('t2','s',1,'other');");
            var spec = new UniqueIndexSpec("Widgets", "IX_Widgets_Tenant_Slug", ["Tenant", "Slug"], "Rev DESC, rowid DESC");

            var repair = Assert.Single(Repair(db, [.. UniqueIndexGuard.Specs, spec]));

            Assert.Equal(("Widgets", "IX_Widgets_Tenant_Slug", 1, 2), (repair.Table, repair.IndexName, repair.DuplicateGroups, repair.RowsRemoved));
            Assert.Equal("newest", Rows(db, "SELECT Body FROM Widgets WHERE Tenant = 't1'").Single()[0]);
            Assert.Equal(2, WorkspaceFixtures.Scalar(db, "SELECT COUNT(*) FROM Widgets"));
            Assert.Equal(2, File.ReadAllLines(Directory.GetFiles(ReportsDirectory, "upgrade-removed-rows-*.jsonl").Single()).Length);
        }

        // ---- helpers ----

        private static SqliteConnection Open(string path, bool readOnly = false)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }

        private static List<object?[]> Rows(string path, string sql)
        {
            using var connection = Open(path, readOnly: true);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            var rows = new List<object?[]>();
            while (reader.Read())
            {
                var row = new object?[reader.FieldCount];
                for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                rows.Add(row);
            }

            return rows;
        }
    }
}
