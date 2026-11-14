using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using AzureDesignStudio.Core.Tests.Support;
using AzureDesignStudio.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// The startup state machine: a corrupt workspace is quarantined (moved, never deleted) and replaced by a
    /// fresh one with a notice; a file that merely cannot be inspected is never touched; every existing
    /// database is backed up before it is migrated.
    /// </summary>
    public class WorkspaceStartupTests : IDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 30, 45, TimeSpan.Zero);

        private readonly TempWorkspaceDirectory _temp = new();

        public void Dispose() => _temp.Dispose();

        private string WorkspacePath => _temp.Path("workspace.db");

        private string BackupsPath => _temp.Path("backups");

        private WorkspaceStartupOptions Options(string? path = null) =>
            new(path ?? WorkspacePath, BackupsPath, new FixedClock(Now));

        /// <summary>A healthy, checkpointed workspace file (several pages) with 40 designs and no -wal or -shm next to it.</summary>
        private string HealthyFile()
        {
            var path = WorkspacePath;
            var workspace = new WorkspaceDb(path);
            using (var ctx = workspace.CreateContext())
            {
                for (var i = 0; i < 40; i++)
                    ctx.Designs.Add(new DesignRecord { Name = "Design " + i, Data = new string('x', 2000), SavedAt = DateTimeOffset.UtcNow });
                ctx.SaveChanges();
            }
            SqliteConnection.ClearAllPools();   // the last connection closing checkpoints and removes the WAL
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
            return path;
        }

        private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

        private static void Truncate(string path, long length)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
            stream.SetLength(length);
        }

        private string[] Quarantined(string suffix = ".db") =>
            Directory.GetFiles(_temp.Root, "workspace.corrupt-*" + suffix);

        [Fact]
        public async Task ACorruptWorkspace_IsMovedAside_AFreshOneIsCreated_AndTheNoticeNamesBoth()
        {
            var path = HealthyFile();
            var backup = await WorkspaceBackup.CreateAsync(path, BackupsPath, BackupReason.Manual, Now.AddHours(-1));
            Truncate(path, 2 * 4096 + 1000);   // not a whole number of pages
            var damagedHash = Hash(path);

            var result = WorkspaceStartup.Open(Options());
            SqliteConnection.ClearAllPools();

            Assert.Equal(WorkspaceStartupOutcome.RecoveredFromCorruption, result.Report.Outcome);

            var quarantined = Assert.Single(Quarantined());
            Assert.Equal(damagedHash, Hash(quarantined));   // moved, not altered
            Assert.Equal("workspace.corrupt-20261114T123045Z.db", Path.GetFileName(quarantined));

            Assert.Equal(IntegrityOutcome.Ok, WorkspaceIntegrity.Check(path).Outcome);
            using (var ctx = result.Db.CreateContext())
                Assert.Equal(0, ctx.Designs.Count());

            var notice = result.Report.Notice;
            Assert.NotNull(notice);
            Assert.Equal(quarantined, notice!.QuarantinePath);
            Assert.Contains(quarantined, notice.Message);
            Assert.Contains(Path.GetFileName(backup.Path), notice.Message);
            Assert.Single(notice.Backups);
        }

        [Fact]
        public void AZeroByteMainFile_WithAWalAndAShm_MovesAllThree()
        {
            var path = WorkspacePath;
            File.WriteAllBytes(path, []);
            File.WriteAllBytes(path + "-wal", new byte[4096]);
            File.WriteAllBytes(path + "-shm", new byte[32768]);

            var result = WorkspaceStartup.Open(Options());
            SqliteConnection.ClearAllPools();

            Assert.Equal(WorkspaceStartupOutcome.RecoveredFromCorruption, result.Report.Outcome);
            var main = Assert.Single(Quarantined());
            Assert.Equal(0, new FileInfo(main).Length);
            Assert.Equal(4096, new FileInfo(main + "-wal").Length);
            Assert.Equal(32768, new FileInfo(main + "-shm").Length);
            Assert.Equal(IntegrityOutcome.Ok, WorkspaceIntegrity.Check(path).Outcome);
        }

        [Fact]
        public void AHeaderZeroedFile_IsQuarantined()
        {
            var path = HealthyFile();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
                stream.Write(new byte[100]);
            var damagedHash = Hash(path);

            var result = WorkspaceStartup.Open(Options());
            SqliteConnection.ClearAllPools();

            Assert.Equal(WorkspaceStartupOutcome.RecoveredFromCorruption, result.Report.Outcome);
            Assert.Equal(damagedHash, Hash(Assert.Single(Quarantined())));
            Assert.Empty(result.Report.Notice!.Backups);
            Assert.Contains("No backups", result.Report.Notice.Message);
        }

        [Fact]
        public void ATextFileNamedWorkspaceDb_IsQuarantined_AndASecondQuarantineGetsANewName()
        {
            var path = WorkspacePath;
            var text = "this is not a database, it is a note about one, long enough to span a header" + new string('.', 200);
            File.WriteAllText(path, text);
            WorkspaceStartup.Open(Options());
            SqliteConnection.ClearAllPools();

            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
            File.WriteAllText(path, text + "again");
            var second = WorkspaceStartup.Open(Options());   // same clock second: the name must not collide
            SqliteConnection.ClearAllPools();

            Assert.Equal(WorkspaceStartupOutcome.RecoveredFromCorruption, second.Report.Outcome);
            var files = Quarantined().Select(f => Path.GetFileName(f)!).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(["workspace.corrupt-20261114T123045Z-1.db", "workspace.corrupt-20261114T123045Z.db"], files);
            Assert.Equal(text, File.ReadAllText(Path.Combine(_temp.Root, "workspace.corrupt-20261114T123045Z.db")));
        }

        [Fact]
        public void AMissingFile_IsFresh_AndCreatesNoBackupsFolder()
        {
            var result = WorkspaceStartup.Open(Options());
            SqliteConnection.ClearAllPools();

            Assert.Equal(WorkspaceStartupOutcome.Fresh, result.Report.Outcome);
            Assert.Null(result.Report.Notice);
            Assert.True(result.Report.Upgrade.Created);
            Assert.True(File.Exists(WorkspacePath));
            Assert.False(Directory.Exists(BackupsPath));
            Assert.Empty(Quarantined());
        }

        [Fact]
        public void AnInconclusiveCheck_StopsStartup_AndMovesNothing()
        {
            var path = HealthyFile();
            var before = Hash(path);
            var options = new WorkspaceStartupOptions(path, BackupsPath, new FixedClock(Now))
            {
                Integrity = _ => new IntegrityResult(IntegrityOutcome.Inconclusive, ["SQLite error 5: database is locked"]),
            };

            var thrown = Assert.Throws<WorkspaceUpgradeException>(() => WorkspaceStartup.Open(options));

            Assert.Contains(path, thrown.Message);
            Assert.Contains("Nothing was moved", thrown.Message);
            Assert.Equal(before, Hash(path));
            Assert.Empty(Quarantined());
            Assert.False(Directory.Exists(BackupsPath));
        }

        [Fact]
        public void AHealthyCurrentFile_IsOpened_WithItsRowsIntact_AndNothingQuarantined()
        {
            var path = HealthyFile();

            var result = WorkspaceStartup.Open(Options());

            Assert.Equal(WorkspaceStartupOutcome.Opened, result.Report.Outcome);
            Assert.Null(result.Report.Notice);
            Assert.False(result.Report.Upgrade.Created);
            Assert.Empty(result.Report.Upgrade.AppliedMigrations);
            Assert.Equal(path, result.Db.DatabasePath);
            using (var ctx = result.Db.CreateContext())
                Assert.Equal(40, ctx.Designs.Count());
            Assert.Empty(Quarantined());
        }

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }
    }
}
