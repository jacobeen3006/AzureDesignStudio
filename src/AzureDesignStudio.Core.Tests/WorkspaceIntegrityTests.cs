using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AzureDesignStudio.Core.Tests.Support;
using AzureDesignStudio.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// What <see cref="WorkspaceIntegrity"/> calls corrupt, what it calls fine, and the rule that a locked,
    /// unreadable or full file is never classified as corruption (so a healthy workspace that another
    /// process holds can never be quarantined).
    ///
    /// Not asserted on purpose: that a bit flip in the middle of the data fails <c>quick_check</c>. It can
    /// pass (the documented blind spot: <c>quick_check</c> checks neither UNIQUE constraints nor that index
    /// content matches table content; the restore verification uses <c>Full</c>), and a test that depends
    /// on where the flipped byte lands would be flaky.
    /// </summary>
    public class WorkspaceIntegrityTests : IDisposable
    {
        private readonly TempWorkspaceDirectory _temp = new();

        public void Dispose() => _temp.Dispose();

        /// <summary>A healthy, checkpointed workspace file (several pages) with no -wal or -shm next to it.</summary>
        private string HealthyFile(string name = "workspace.db")
        {
            var path = _temp.Path(name);
            var workspace = new WorkspaceDb(path);
            using (var ctx = workspace.CreateContext())
            {
                for (var i = 0; i < 40; i++)
                    ctx.Designs.Add(new DesignRecord { Name = "Design " + i, Data = new string('x', 2000), SavedAt = DateTimeOffset.UtcNow });
                ctx.SaveChanges();
            }
            SqliteConnection.ClearAllPools();   // the last connection closing checkpoints and removes the WAL
            Assert.False(File.Exists(path + "-wal") && new FileInfo(path + "-wal").Length > 0, "precondition: a checkpointed file");
            File.Delete(path + "-wal");
            File.Delete(path + "-shm");
            return path;
        }

        private static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

        [Fact]
        public void AHealthyFile_IsOkAtBothDepths_AndIsNotModified()
        {
            var path = HealthyFile();
            var before = Hash(path);

            Assert.Equal(IntegrityOutcome.Ok, WorkspaceIntegrity.Check(path, IntegrityDepth.Quick).Outcome);
            Assert.Equal(IntegrityOutcome.Ok, WorkspaceIntegrity.Check(path, IntegrityDepth.Full).Outcome);

            // the live-file check leaves the file and its directory as it found them
            Assert.Equal(before, Hash(path));
            Assert.False(File.Exists(path + "-wal"));
            Assert.False(File.Exists(path + "-shm"));

            // a read-only snapshot check never changes the file either (SQLite may leave an empty -wal/-shm
            // pair beside a WAL-mode file it could not clean up, which is why backups are delete-mode files)
            Assert.Equal(IntegrityOutcome.Ok, WorkspaceIntegrity.Check(path, IntegrityDepth.Full, IntegrityAccess.Snapshot).Outcome);
            Assert.Equal(before, Hash(path));
        }

        [Fact]
        public void AFileTruncatedToANonWholePageCount_IsCorrupt()
        {
            var path = HealthyFile();
            var length = new FileInfo(path).Length;
            Assert.True(length > 3 * 4096, "precondition: several pages");
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
                stream.SetLength(2 * 4096 + 1000);

            var result = WorkspaceIntegrity.Check(path);

            Assert.Equal(IntegrityOutcome.Corrupt, result.Outcome);
            Assert.NotEmpty(result.Problems);
        }

        [Fact]
        public void AFileWhoseHeaderIsZeroed_IsCorrupt_AtBothDepths()
        {
            var path = HealthyFile();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write))
                stream.Write(new byte[100]);

            Assert.Equal(IntegrityOutcome.Corrupt, WorkspaceIntegrity.Check(path, IntegrityDepth.Quick).Outcome);
            Assert.Equal(IntegrityOutcome.Corrupt, WorkspaceIntegrity.Check(path, IntegrityDepth.Full).Outcome);
            Assert.Equal(IntegrityOutcome.Corrupt, WorkspaceIntegrity.Check(path, IntegrityDepth.Full, IntegrityAccess.Snapshot).Outcome);
        }

        [Fact]
        public void APlainTextFileNamedDb_IsCorrupt()
        {
            var path = _temp.Path("notes.db");
            File.WriteAllText(path, "this is not a database, it is a note about one, long enough to span a header" + new string('.', 200));

            Assert.Equal(IntegrityOutcome.Corrupt, WorkspaceIntegrity.Check(path).Outcome);
        }

        [Fact]
        public void AZeroByteFileWithoutAWal_IsAValidEmptyDatabase()
        {
            var path = _temp.Path("empty.db");
            File.WriteAllBytes(path, []);

            Assert.Equal(IntegrityOutcome.Ok, WorkspaceIntegrity.Check(path).Outcome);
            Assert.Equal(0, new FileInfo(path).Length);
            Assert.False(File.Exists(path + "-wal"));
        }

        [Fact]
        public void AZeroByteFileNextToANonEmptyWal_IsCorrupt_AndTheProblemNamesTheWal()
        {
            var path = _temp.Path("lost.db");
            File.WriteAllBytes(path, []);
            File.WriteAllBytes(path + "-wal", new byte[4096]);

            var result = WorkspaceIntegrity.Check(path);

            Assert.Equal(IntegrityOutcome.Corrupt, result.Outcome);
            Assert.Contains(result.Problems, p => p.Contains("write-ahead", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void AZeroByteFileNextToAnEmptyWal_IsStillAValidEmptyDatabase()
        {
            var path = _temp.Path("empty.db");
            File.WriteAllBytes(path, []);
            File.WriteAllBytes(path + "-wal", []);

            Assert.Equal(IntegrityOutcome.Ok, WorkspaceIntegrity.Check(path).Outcome);
        }

        [Fact]
        public void AMissingPath_IsMissing_AndIsNotCreated()
        {
            var path = _temp.Path("absent.db");

            Assert.Equal(IntegrityOutcome.Missing, WorkspaceIntegrity.Check(path).Outcome);
            Assert.Equal(IntegrityOutcome.Missing, WorkspaceIntegrity.Check(path, IntegrityDepth.Full, IntegrityAccess.Snapshot).Outcome);
            Assert.False(File.Exists(path));
        }

        [Fact]
        public void AFileTheProcessCannotRead_IsInconclusive_NeverCorrupt()
        {
            if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;   // needs POSIX modes, and root ignores them
            var path = HealthyFile();
            File.SetUnixFileMode(path, UnixFileMode.None);
            try
            {
                var result = WorkspaceIntegrity.Check(path);

                Assert.Equal(IntegrityOutcome.Inconclusive, result.Outcome);
                Assert.NotEmpty(result.Problems);
            }
            finally
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            // once readable again the very same file is fine: the failure said nothing about its content
            Assert.Equal(IntegrityOutcome.Ok, WorkspaceIntegrity.Check(path).Outcome);
        }

        [Theory]
        [InlineData(3, true)]    // perm
        [InlineData(5, true)]    // busy
        [InlineData(6, true)]    // locked
        [InlineData(7, true)]    // nomem
        [InlineData(8, true)]    // readonly
        [InlineData(10, true)]   // ioerr
        [InlineData(13, true)]   // full
        [InlineData(14, true)]   // cantopen
        [InlineData(11, false)]  // corrupt
        [InlineData(26, false)]  // notadb
        [InlineData(99, false)]  // unknown: not known to be environmental, so it is not trusted as harmless
        public void ContentionPermissionAndIoCodes_AreInconclusive_EverythingElseIsNot(int code, bool inconclusive)
        {
            Assert.Equal(inconclusive, IntegrityCodes.IsInconclusive(code));
        }

        [Fact]
        public void TheClassificationTable_CoversExactlyTheEightEnvironmentalCodes()
        {
            var inconclusive = Enumerable.Range(0, 300).Where(IntegrityCodes.IsInconclusive).ToArray();

            Assert.Equal(new[] { 3, 5, 6, 7, 8, 10, 13, 14 }, inconclusive);
        }
    }
}
