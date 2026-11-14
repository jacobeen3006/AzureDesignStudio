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

        // ---- Task 2: verification rules ----------------------------------------------------------------

        /// <summary>A real backup (3 designs) with its manifest, in a folder away from the workspace.</summary>
        private async Task<BackupResult> BaseBackupAsync()
        {
            var db = WorkspaceStartup.Open(Options()).Db;
            AddDesigns(db, 3, "d-");
            SqliteConnection.ClearAllPools();
            return await WorkspaceBackup.CreateAsync(WorkspacePath, _temp.Path("source-backups"), BackupReason.Manual, Now.AddHours(-1));
        }

        /// <summary>Copies the backup to a fresh path (no sidecar) and lets <paramref name="mutate"/> change it on an unpooled connection.</summary>
        private string MutatedCopy(BackupResult backup, string name, Action<SqliteConnection> mutate)
        {
            var path = _temp.Path(name);
            File.Copy(backup.Path, path);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString()))
            {
                connection.Open();
                mutate(connection);
            }

            return path;
        }

        private static void Exec(SqliteConnection connection, string sql)
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.ExecuteNonQuery();
        }

        private async Task<RestoreVerification> StageAndExpectRejectedAsync(string picked)
        {
            var verdict = await RestoreStaging.StageAsync(picked, _temp.Root);
            Assert.False(verdict.Accepted);
            Assert.NotEmpty(verdict.Reasons);
            Assert.Null(RestoreStaging.GetPending(_temp.Root));
            Assert.False(Directory.Exists(_temp.Path("restore-staging")));
            return verdict;
        }

        [Fact]
        public async Task ACandidateWithATrigger_IsRejected()
        {
            var backup = await BaseBackupAsync();
            var picked = MutatedCopy(backup, "trigger.db", c =>
                Exec(c, "CREATE TRIGGER evil AFTER INSERT ON \"Designs\" BEGIN SELECT 1; END"));

            var verdict = await StageAndExpectRejectedAsync(picked);

            Assert.Contains(verdict.Reasons, r => r.Contains("trigger", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task ACandidateWithAView_IsRejected()
        {
            var backup = await BaseBackupAsync();
            var picked = MutatedCopy(backup, "view.db", c => Exec(c, "CREATE VIEW evil AS SELECT * FROM \"Designs\""));

            var verdict = await StageAndExpectRejectedAsync(picked);

            Assert.Contains(verdict.Reasons, r => r.Contains("view", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task ACandidateWithAVirtualTable_IsRejected()
        {
            var backup = await BaseBackupAsync();
            var picked = MutatedCopy(backup, "virtual.db", c => Exec(c, "CREATE VIRTUAL TABLE evil USING fts5(body)"));

            var verdict = await StageAndExpectRejectedAsync(picked);

            Assert.Contains(verdict.Reasons, r => r.Contains("virtual", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task ACandidateWithATableOutsideTheModel_IsRejected()
        {
            var backup = await BaseBackupAsync();
            var picked = MutatedCopy(backup, "unknown.db", c => Exec(c, "CREATE TABLE \"Evil\" (\"Id\" INTEGER)"));

            var verdict = await StageAndExpectRejectedAsync(picked);

            Assert.Contains(verdict.Reasons, r => r.Contains("Evil", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ACandidateFromANewerSchema_IsRejected_WithAReasonThatSaysSo()
        {
            var backup = await BaseBackupAsync();
            var picked = MutatedCopy(backup, "newer.db", c => Exec(c,
                "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('99991231235959_FromTheFuture', '99.0.0')"));

            var verdict = await StageAndExpectRejectedAsync(picked);

            Assert.Contains(verdict.Reasons, r => r.Contains("newer", StringComparison.OrdinalIgnoreCase)
                && r.Contains("99991231235959_FromTheFuture", StringComparison.Ordinal));
        }

        private string PickedWithSidecar(BackupResult backup, string name, Func<BackupManifest, BackupManifest> edit)
        {
            var picked = _temp.Path(name);
            File.Copy(backup.Path, picked);
            edit(BackupManifest.Read(backup.ManifestPath)).Write(BackupManifest.PathFor(picked));
            return picked;
        }

        [Fact]
        public async Task ACandidateWithAMatchingManifest_IsAccepted_AndTheManifestIsStagedWithIt()
        {
            var backup = await BaseBackupAsync();
            var picked = PickedWithSidecar(backup, "ok.db", m => m);

            var verdict = await RestoreStaging.StageAsync(picked, _temp.Root);

            Assert.True(verdict.Accepted, string.Join("; ", verdict.Reasons));
            Assert.True(File.Exists(_temp.Path("restore-staging/candidate.db.manifest.json")));
        }

        [Fact]
        public async Task ACandidateWhoseManifestRowCountDiffers_IsRejected()
        {
            var backup = await BaseBackupAsync();
            var picked = PickedWithSidecar(backup, "counts.db", m =>
                m with { TableCounts = m.TableCounts.ToDictionary(c => c.Key, c => c.Key == "Designs" ? c.Value + 1 : c.Value) });

            var verdict = await StageAndExpectRejectedAsync(picked);

            Assert.Contains(verdict.Reasons, r => r.Contains("Designs", StringComparison.Ordinal)
                && r.Contains("row", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task ACandidateWhoseManifestSha256Differs_IsRejected()
        {
            var backup = await BaseBackupAsync();
            var picked = PickedWithSidecar(backup, "sha.db", m => m with { Sha256 = new string('0', 64) });

            var verdict = await StageAndExpectRejectedAsync(picked);

            Assert.Contains(verdict.Reasons, r => r.Contains("SHA-256", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ACandidateWithoutAManifest_IsAccepted_WithANote()
        {
            var backup = await BaseBackupAsync();
            var picked = MutatedCopy(backup, "bare.db", _ => { });

            var verdict = await RestoreStaging.StageAsync(picked, _temp.Root);

            Assert.True(verdict.Accepted, string.Join("; ", verdict.Reasons));
            Assert.Contains(verdict.Reasons, r => r.Contains("manifest", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task AZeroByteFile_IsRejected()
        {
            var picked = _temp.Path("zero.db");
            File.WriteAllBytes(picked, []);
            await StageAndExpectRejectedAsync(picked);
        }

        [Fact]
        public async Task ATextFile_IsRejected()
        {
            var picked = _temp.Path("text.db");
            File.WriteAllText(picked, "this is not a database\n" + new string('a', 9000));
            await StageAndExpectRejectedAsync(picked);
        }

        [Fact]
        public async Task ATruncatedBackup_IsRejected()
        {
            var backup = await BaseBackupAsync();
            var picked = _temp.Path("truncated.db");
            File.Copy(backup.Path, picked);
            using (var stream = new FileStream(picked, FileMode.Open, FileAccess.Write))
                stream.SetLength(2 * 4096 + 1000);

            await StageAndExpectRejectedAsync(picked);
        }

        [Fact]
        public async Task AHeaderValidFileThatFailsFullIntegrity_IsRejected()
        {
            var backup = await BaseBackupAsync();
            var picked = _temp.Path("damaged.db");
            File.Copy(backup.Path, picked);
            using (var stream = new FileStream(picked, FileMode.Open, FileAccess.Write))
            {
                stream.Position = 3 * 4096;
                stream.Write(Enumerable.Repeat((byte)0xFF, 4096).ToArray());
            }

            var verdict = await StageAndExpectRejectedAsync(picked);

            Assert.Contains(verdict.Reasons, r => r.Contains("integrity", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task ALegacyCandidateWithNoHistoryTable_IsAccepted_AndUpgradedWithAPremigrationBackupAtTheNextStart()
        {
            WorkspaceStartup.Open(Options());
            SqliteConnection.ClearAllPools();
            var picked = _temp.Path("legacy-l2.db");
            WorkspaceFixtures.Load(WorkspaceFixtures.L2PreGovernance, picked);

            var verdict = await RestoreStaging.StageAsync(picked, _temp.Root);
            Assert.True(verdict.Accepted, string.Join("; ", verdict.Reasons));

            var result = WorkspaceStartup.Open(Options());
            SqliteConnection.ClearAllPools();

            Assert.True(result.Report.Restore!.Applied);
            Assert.Equal(WorkspaceStartupOutcome.Upgraded, result.Report.Outcome);
            var premigration = Assert.Single(WorkspaceBackup.List(BackupsPath), b => b.Reason == BackupReason.Premigration);
            Assert.True(File.Exists(premigration.Path));
            Assert.Equal(0, WorkspaceFixtures.Scalar(WorkspacePath, "SELECT COUNT(*) FROM \"GovernanceFindings\""));
            Assert.Equal(WorkspaceFixtures.ExpectedCounts(WorkspaceFixtures.L2PreGovernance)["Designs"], DesignCount(WorkspacePath));
        }

        [Fact]
        public async Task ACandidateModifiedAfterStaging_IsDiscardedAtApply_AndTheLiveWorkspaceIsUntouched()
        {
            var backup = await BaseBackupAsync();
            Assert.True((await RestoreStaging.StageAsync(backup.Path, _temp.Root)).Accepted);
            var liveBefore = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(WorkspacePath)));
            using (var stream = new FileStream(_temp.Path("restore-staging/candidate.db"), FileMode.Append, FileAccess.Write))
                stream.WriteByte(0x42);

            var outcome = await RestoreStaging.ApplyPendingAsync(WorkspacePath, _temp.Root, new FixedClock(Now));

            Assert.NotNull(outcome);
            Assert.False(outcome.Applied);
            Assert.Contains("changed", outcome.Problem, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(liveBefore, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(WorkspacePath))));
            Assert.False(Directory.Exists(_temp.Path("restore-staging")));
            Assert.Empty(Directory.GetFiles(_temp.Root, "workspace.pre-restore-*"));
        }

        [Fact]
        public async Task WhenOnlyAWalRemainsOfTheLiveWorkspace_TheRestoreStillApplies_AndStagingIsCleaned()
        {
            // WR-03: the first move was the -wal, so there was no workspace.pre-restore-*.db to restrict and the
            // permission call threw after the candidate was already in place
            var backup = await BaseBackupAsync();
            Assert.True((await RestoreStaging.StageAsync(backup.Path, _temp.Root)).Accepted);
            SqliteConnection.ClearAllPools();
            File.Delete(WorkspacePath);
            File.Delete(WorkspacePath + "-shm");
            File.WriteAllBytes(WorkspacePath + "-wal", new byte[64]);

            var outcome = await RestoreStaging.ApplyPendingAsync(WorkspacePath, _temp.Root, new FixedClock(Now));

            Assert.NotNull(outcome);
            Assert.True(outcome.Applied, outcome.Problem);
            Assert.True(File.Exists(WorkspacePath));
            Assert.False(File.Exists(WorkspacePath + "-wal"));
            Assert.True(File.Exists(_temp.Path("workspace.pre-restore-20261114T123045Z.db-wal")));
            Assert.False(Directory.Exists(_temp.Path("restore-staging")));
            Assert.Null(RestoreStaging.GetPending(_temp.Root));
        }

        [Fact]
        public async Task CancelPending_RemovesTheStagingFolderAndNothingElse()
        {
            var backup = await BaseBackupAsync();
            Assert.True((await RestoreStaging.StageAsync(backup.Path, _temp.Root)).Accepted);
            var workspaceBefore = File.ReadAllBytes(WorkspacePath);
            Assert.NotNull(RestoreStaging.GetPending(_temp.Root));

            RestoreStaging.CancelPending(_temp.Root);

            Assert.Null(RestoreStaging.GetPending(_temp.Root));
            Assert.False(Directory.Exists(_temp.Path("restore-staging")));
            Assert.True(File.Exists(backup.Path));
            Assert.True(File.Exists(backup.ManifestPath));
            Assert.Equal(workspaceBefore, File.ReadAllBytes(WorkspacePath));
        }

        private sealed class FixedClock(DateTimeOffset now) : TimeProvider
        {
            public override DateTimeOffset GetUtcNow() => now;
        }
    }
}
