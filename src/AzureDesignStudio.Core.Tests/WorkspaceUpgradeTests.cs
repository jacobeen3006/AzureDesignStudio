using System;
using AzureDesignStudio.Core.Tests.Support;
using AzureDesignStudio.Workspace;
using Microsoft.Data.Sqlite;
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
