using System;
using System.Collections.Generic;
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
    /// The backup service: a <c>VACUUM INTO</c> snapshot of a live WAL workspace that is verified, hashed,
    /// manifested, placed atomically and listed.
    /// </summary>
    public class WorkspaceBackupTests : IDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 30, 45, TimeSpan.Zero);

        private readonly TempWorkspaceDirectory _temp = new();

        public void Dispose() => _temp.Dispose();

        private static SqliteConnection OpenReadOnly(string path)
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            return connection;
        }

        private static string Pragma(string path, string pragma)
        {
            using var connection = OpenReadOnly(path);
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA " + pragma;
            return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
        }

        /// <summary>A workspace with <paramref name="designs"/> designs whose commits are still in the -wal file.</summary>
        private async Task<(string Path, SqliteConnection Holder)> LiveWalWorkspace(int designs)
        {
            var path = _temp.Path("workspace.db");
            var workspace = new WorkspaceDb(path);
            // An open connection keeps SQLite from checkpointing and deleting the WAL when the last other one closes.
            var holder = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            holder.Open();
            using (var command = holder.CreateCommand())
            {
                command.CommandText = "SELECT COUNT(*) FROM \"Designs\"";
                command.ExecuteScalar();
            }

            await using var ctx = workspace.CreateContext();
            for (var i = 0; i < designs; i++)
                ctx.Designs.Add(new DesignRecord { Name = "Design " + i, Data = "{\"i\":" + i + "}", SavedAt = Now });
            await ctx.SaveChangesAsync();
            return (path, holder);
        }

        [Fact]
        public async Task BackupOfALiveWalWorkspace_ContainsUncheckpointedRows_AndIsVerifiedHashedAndListed()
        {
            var (live, holder) = await LiveWalWorkspace(designs: 4);
            using var keepWal = holder;
            Assert.True(new FileInfo(live + "-wal").Length > 0, "precondition: the rows must exist only in the -wal file");
            var backups = _temp.Path("O'Brien");   // an apostrophe must work: the path is a bound parameter

            var result = await WorkspaceBackup.CreateAsync(live, backups, BackupReason.Manual, Now);

            // the backup holds every design and is a rollback-journal file
            Assert.Equal(4, WorkspaceFixtures.Scalar(result.Path, "SELECT COUNT(*) FROM \"Designs\""));
            Assert.Equal("delete", Pragma(result.Path, "journal_mode"));
            Assert.Equal(IntegrityOutcome.Ok, WorkspaceIntegrity.Check(result.Path, IntegrityDepth.Quick, IntegrityAccess.Snapshot).Outcome);

            // the manifest matches the live data and the file
            var manifest = BackupManifest.Read(result.ManifestPath);
            Assert.Equal(BackupReason.Manual, manifest.Reason);
            Assert.Equal(Now, manifest.CreatedUtc);
            Assert.Equal(4, manifest.TableCounts["Designs"]);
            foreach (var (table, count) in WorkspaceFixtures.TableCounts(live))
                Assert.Equal(count, manifest.TableCounts[table]);
            Assert.NotNull(manifest.LatestMigrationId);
            Assert.False(string.IsNullOrWhiteSpace(manifest.AppVersion));
            Assert.Equal("workspace.db", manifest.SourceFileName);
            Assert.Equal(new FileInfo(result.Path).Length, manifest.SizeBytes);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(result.Path))), manifest.Sha256);

            // placed under the strict name, with its sidecar, and no temp files left behind
            Assert.Equal("workspace-20261114T123045Z-manual.db", Path.GetFileName(result.Path));
            Assert.Equal(result.Path + ".manifest.json", result.ManifestPath);
            Assert.Empty(Directory.EnumerateFiles(backups, "*.tmp"));

            var listed = Assert.Single(WorkspaceBackup.List(backups));
            Assert.Equal(result.Path, listed.Path);
            Assert.Equal(BackupReason.Manual, listed.Reason);
            Assert.Equal(Now, listed.CreatedUtc);
            Assert.Equal(manifest.SizeBytes, listed.SizeBytes);
        }

        [Fact]
        public async Task AnExistingTarget_IsRefusedAndLeftUntouched()
        {
            var (live, holder) = await LiveWalWorkspace(designs: 1);
            using var keepWal = holder;
            var backups = _temp.Path("backups");
            var first = await WorkspaceBackup.CreateAsync(live, backups, BackupReason.Manual, Now);
            var before = File.ReadAllBytes(first.Path);

            await Assert.ThrowsAsync<IOException>(() =>
                WorkspaceBackup.CreateAsync(live, backups, BackupReason.Manual, Now));

            Assert.Equal(before, File.ReadAllBytes(first.Path));
            Assert.Equal(first.Manifest.Sha256, BackupManifest.Read(first.ManifestPath).Sha256);
            Assert.Empty(Directory.EnumerateFiles(backups, "*.tmp"));
        }

        [Fact]
        public async Task TwoCallsForTheSameSecond_NeverCorruptEachOther_TheManifestMatchesTheDatabaseThatExists()
        {
            // WR-07: a double click on "Back up now" shared one .tmp path and replaced the manifest before the database
            var (live, holder) = await LiveWalWorkspace(designs: 3);
            using var keepWal = holder;
            var backups = _temp.Path("backups");

            var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
            {
                try { return (BackupResult?)await WorkspaceBackup.CreateAsync(live, backups, BackupReason.Manual, Now); }
                catch (IOException) { return null; }
            }));

            var winner = Assert.Single(attempts, a => a is not null)!;
            var manifest = BackupManifest.Read(winner.ManifestPath);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(winner.Path))), manifest.Sha256);
            Assert.Equal(3, manifest.TableCounts["Designs"]);
            Assert.Empty(Directory.EnumerateFiles(backups, "*.tmp"));
            Assert.Single(Directory.EnumerateFiles(backups, "*.manifest.json"));
            Assert.Single(Directory.EnumerateFiles(backups, "*.db"));
        }

        [Fact]
        public async Task Prune_RemovesAManifestWhoseBackupIsGone_ButNotOneThatBelongsToABackup_NorAStrangerFile()
        {
            var backups = _temp.Path("backups");
            Directory.CreateDirectory(backups);
            var orphan = Path.Combine(backups, "workspace-20260101T000000Z-scheduled.db.manifest.json");
            var paired = Path.Combine(backups, "workspace-20260102T000000Z-scheduled.db");
            var pairedManifest = paired + ".manifest.json";
            var stranger = Path.Combine(backups, "notes.db.manifest.json");
            var otherStem = Path.Combine(backups, "deploy-20260101T000000Z-scheduled.db.manifest.json");
            foreach (var file in new[] { orphan, paired, pairedManifest, stranger, otherStem })
                File.WriteAllText(file, "x");

            await WorkspaceBackup.PruneAsync(backups, Now);

            Assert.False(File.Exists(orphan));
            Assert.True(File.Exists(paired));
            Assert.True(File.Exists(pairedManifest));
            Assert.True(File.Exists(stranger));
            Assert.True(File.Exists(otherStem));
        }

        [Fact]
        public async Task AMissingSource_ThrowsFileNotFound_AndCreatesNothing()
        {
            var backups = _temp.Path("backups");

            await Assert.ThrowsAsync<FileNotFoundException>(() =>
                WorkspaceBackup.CreateAsync(_temp.Path("absent.db"), backups, BackupReason.Manual, Now));

            Assert.False(Directory.Exists(backups));
            Assert.False(File.Exists(_temp.Path("absent.db")));
        }

        [Fact]
        public async Task NotEnoughFreeSpace_IsRefusedBeforeAnyWork()
        {
            var (live, holder) = await LiveWalWorkspace(designs: 1);
            using var keepWal = holder;
            var backups = _temp.Path("backups");

            var e = await Assert.ThrowsAsync<IOException>(() =>
                WorkspaceBackup.CreateAsync(live, backups, BackupReason.Premigration, Now, "workspace", _ => 1, default));

            Assert.Contains("short by", e.Message);
            Assert.Empty(Directory.EnumerateFiles(backups));
        }

        [Fact]
        public async Task TheBackupSourceStemIsHonoured_AndListIgnoresOtherStems()
        {
            var (live, holder) = await LiveWalWorkspace(designs: 1);
            using var keepWal = holder;
            var backups = _temp.Path("backups");

            var deploy = await WorkspaceBackup.CreateAsync(live, backups, BackupReason.Premigration, Now, "deploy");
            await WorkspaceBackup.CreateAsync(live, backups, BackupReason.Scheduled, Now.AddMinutes(1));

            Assert.Equal("deploy-20261114T123045Z-premigration.db", Path.GetFileName(deploy.Path));
            Assert.Equal(deploy.Path, Assert.Single(WorkspaceBackup.List(backups, "deploy")).Path);
            Assert.Equal(BackupReason.Scheduled, Assert.Single(WorkspaceBackup.List(backups)).Reason);
        }

        [Fact]
        public void ListOfAMissingDirectory_IsEmpty() =>
            Assert.Empty(WorkspaceBackup.List(_temp.Path("never-created")));

        [Fact]
        public async Task OnUnix_TheDirectoryAndFilesAreUserOnly()
        {
            if (OperatingSystem.IsWindows()) return;   // profile ACLs apply there
            var (live, holder) = await LiveWalWorkspace(designs: 1);
            using var keepWal = holder;
            var backups = _temp.Path("backups");

            var result = await WorkspaceBackup.CreateAsync(live, backups, BackupReason.Manual, Now);

            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(backups));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(result.Path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(result.ManifestPath));
        }
    }
}
