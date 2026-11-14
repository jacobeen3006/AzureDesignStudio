using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using AzureDesignStudio.Core.Tests.Support;
using AzureDesignStudio.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// An abandoned <c>__EFMigrationsLock</c> row must not hang startup: the bounded upgrade fails with the exact
    /// recovery statement, never deletes the lock itself, and the documented manual recovery works.
    /// </summary>
    public class MigrationLockTests : IDisposable
    {
        private readonly TempWorkspaceDirectory _temp = new();

        public void Dispose() => _temp.Dispose();

        private string LockedLegacyDatabase()
        {
            var path = _temp.Path("locked.db");
            WorkspaceFixtures.Load(WorkspaceFixtures.L3CurrentLegacy, path);
            using var connection = Open(path);
            Exec(connection, "CREATE TABLE \"__EFMigrationsLock\" (\"Id\" INTEGER NOT NULL CONSTRAINT \"PK___EFMigrationsLock\" PRIMARY KEY, \"Timestamp\" TEXT NOT NULL);");
            Exec(connection, "INSERT INTO \"__EFMigrationsLock\" (\"Id\", \"Timestamp\") VALUES (1, '2026-03-01 10:00:00+00:00');");
            return path;
        }

        [Fact]
        public async Task AbandonedLock_FailsWithinTheBoundAndNamesTheRecovery()
        {
            var path = LockedLegacyDatabase();
            var before = WorkspaceFixtures.TableCounts(path);
            var backups = _temp.Path("backups-first");
            var options = new WorkspaceUpgradeOptions { MigrateTimeout = TimeSpan.FromSeconds(2), BackupsDirectory = backups };

            var clock = Stopwatch.StartNew();
            var e = await Assert.ThrowsAsync<WorkspaceUpgradeException>(
                () => WorkspaceSchema.UpgradeAsync(path, options, CancellationToken.None));
            clock.Stop();
            SqliteConnection.ClearAllPools();

            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"The upgrade took {clock.Elapsed}; the bound did not hold.");
            Assert.Contains("__EFMigrationsLock", e.Message, StringComparison.Ordinal);
            Assert.Contains("DELETE FROM \"__EFMigrationsLock\"", e.Message, StringComparison.Ordinal);
            Assert.Contains(path, e.Message, StringComparison.Ordinal);

            // The lock is never removed automatically, and nothing else changed.
            Assert.Equal(1, WorkspaceFixtures.Scalar(path, "SELECT COUNT(*) FROM \"__EFMigrationsLock\""));
            Assert.Equal(before, WorkspaceFixtures.TableCounts(path));
            Assert.Single(Directory.GetFiles(backups, "workspace-*-premigration.db"));
        }

        [Fact]
        public async Task ManualRecovery_LetsTheNextUpgradeSucceed()
        {
            var path = LockedLegacyDatabase();
            var before = WorkspaceFixtures.TableCounts(path);
            await Assert.ThrowsAsync<WorkspaceUpgradeException>(() => WorkspaceSchema.UpgradeAsync(
                path,
                new WorkspaceUpgradeOptions { MigrateTimeout = TimeSpan.FromSeconds(2), BackupsDirectory = _temp.Path("backups-stuck") },
                CancellationToken.None));
            SqliteConnection.ClearAllPools();

            using (var connection = Open(path))
                Exec(connection, "DELETE FROM \"__EFMigrationsLock\"");

            // A different folder: the retry may land in the same second as the first attempt's backup.
            var result = await WorkspaceSchema.UpgradeAsync(
                path, new WorkspaceUpgradeOptions { BackupsDirectory = _temp.Path("backups-retry") }, CancellationToken.None);
            SqliteConnection.ClearAllPools();

            Assert.NotEmpty(result.AppliedMigrations);
            Assert.Equal("wal", result.JournalMode);
            Assert.Equal(1, WorkspaceFixtures.Scalar(path, "SELECT COUNT(*) FROM \"__EFMigrationsHistory\""));
            Assert.Equal(before, WorkspaceFixtures.TableCounts(path));
            Assert.Equal(0, WorkspaceFixtures.Scalar(path, "SELECT COUNT(*) FROM \"__EFMigrationsLock\""));
        }

        [Fact]
        public async Task CallerCancellation_IsNotReportedAsAnAbandonedLock()
        {
            var path = _temp.Path("cancel.db");
            WorkspaceFixtures.Load(WorkspaceFixtures.L3CurrentLegacy, path);
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();

            var thrown = await Record.ExceptionAsync(() => WorkspaceSchema.UpgradeAsync(
                path, new WorkspaceUpgradeOptions { BackupsDirectory = _temp.Path("backups-cancel") }, cancelled.Token));

            Assert.NotNull(thrown);
            Assert.IsAssignableFrom<OperationCanceledException>(thrown);
            Assert.IsNotType<WorkspaceUpgradeException>(thrown);
            Assert.DoesNotContain("__EFMigrationsLock", thrown.Message, StringComparison.Ordinal);
        }

        private static SqliteConnection Open(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }

        private static void Exec(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }
    }
}
