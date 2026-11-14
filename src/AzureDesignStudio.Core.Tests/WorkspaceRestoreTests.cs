using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AzureDesignStudio.Core.Tests.Support;
using AzureDesignStudio.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// Restore is staged, verified on the staged copy and applied at the next launch before any connection
    /// exists; the previous workspace is kept and no stale write-ahead log is replayed onto the restored file.
    /// </summary>
    public class WorkspaceRestoreTests : IDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 30, 45, TimeSpan.Zero);

        private readonly TempWorkspaceDirectory _temp = new();

        public void Dispose() => _temp.Dispose();

        private string WorkspacePath => _temp.Path("workspace.db");

        private string BackupsPath => _temp.Path("backups");

        private WorkspaceStartupOptions Options() => new(WorkspacePath, BackupsPath, new FixedClock(Now));

        private static void AddDesigns(WorkspaceDb db, int count, string prefix)
        {
            using var ctx = db.CreateContext();
            for (var i = 0; i < count; i++)
                ctx.Designs.Add(new DesignRecord { Name = prefix + i, Data = new string('x', 2000), SavedAt = DateTimeOffset.UtcNow });
            ctx.SaveChanges();
        }

        private static long DesignCount(string path) => WorkspaceFixtures.Scalar(path, "SELECT COUNT(*) FROM \"Designs\"");

        private static string Pragma(string path, string pragma)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA " + pragma;
            return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? "";
        }

        [Fact]
        public async Task AStagedBackup_IsRestoredAtTheNextStart_TheOldFileIsKept_AndNoStaleWalIsReplayed()
        {
            var first = WorkspaceStartup.Open(Options());
            AddDesigns(first.Db, 3, "kept-");
            SqliteConnection.ClearAllPools();
            var backup = await WorkspaceBackup.CreateAsync(WorkspacePath, BackupsPath, BackupReason.Manual, Now.AddHours(-1));

            // A reader that stays open keeps the 4th design in the live -wal (the last connection to close would checkpoint it away).
            using var reader = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = WorkspacePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            reader.Open();
            using (var begin = reader.CreateCommand())
            {
                begin.CommandText = "BEGIN; SELECT COUNT(*) FROM \"Designs\";";
                begin.ExecuteScalar();
            }

            AddDesigns(first.Db, 1, "lost-");
            SqliteConnection.ClearAllPools();
            Assert.True(new FileInfo(WorkspacePath + "-wal").Length > 0, "the live -wal must hold the 4th design");

            var verdict = await RestoreStaging.StageAsync(backup.Path, _temp.Root);
            Assert.True(verdict.Accepted, string.Join("; ", verdict.Reasons));
            var pending = RestoreStaging.GetPending(_temp.Root);
            Assert.NotNull(pending);
            Assert.Equal(System.IO.Path.GetFileName(backup.Path), pending.SourceName);

            var second = WorkspaceStartup.Open(Options());
            SqliteConnection.ClearAllPools();

            Assert.Equal(3, DesignCount(WorkspacePath));
            Assert.Equal(IntegrityOutcome.Ok, WorkspaceIntegrity.Check(WorkspacePath, IntegrityDepth.Full).Outcome);
            Assert.Equal("wal", Pragma(WorkspacePath, "journal_mode"), ignoreCase: true);

            var restore = second.Report.Restore;
            Assert.NotNull(restore);
            Assert.True(restore.Applied);
            Assert.NotNull(restore.PreRestorePath);
            Assert.Equal("workspace.pre-restore-20261114T123045Z.db", System.IO.Path.GetFileName(restore.PreRestorePath));
            Assert.True(File.Exists(restore.PreRestorePath));
            Assert.Equal(4, DesignCount(restore.PreRestorePath!));   // the previous workspace, with its -wal, is intact

            Assert.False(Directory.Exists(_temp.Path("restore-staging")));
            Assert.Null(RestoreStaging.GetPending(_temp.Root));
        }

        [Fact]
        public async Task AFileWithAZeroedHeader_IsRejected_AndNothingIsStaged()
        {
            var bogus = _temp.Path("bogus.db");
            File.WriteAllBytes(bogus, new byte[8192]);

            var verdict = await RestoreStaging.StageAsync(bogus, _temp.Root);

            Assert.False(verdict.Accepted);
            Assert.NotEmpty(verdict.Reasons);
            Assert.Null(RestoreStaging.GetPending(_temp.Root));
            Assert.False(Directory.Exists(_temp.Path("restore-staging")));
            Assert.False(Directory.Exists(_temp.Path("restore-staging.new")));
        }

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }
    }
}
