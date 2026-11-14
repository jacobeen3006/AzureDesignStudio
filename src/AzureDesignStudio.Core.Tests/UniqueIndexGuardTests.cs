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
