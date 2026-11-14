using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AzureDesignStudio.Core.Tests.Support;
using AzureDesignStudio.Workspace;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// The retention rule (a pure function over backup descriptors), the strict file-name pattern and the
    /// contained pruning that applies them.
    /// </summary>
    public class BackupRetentionTests : IDisposable
    {
        private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

        private readonly TempWorkspaceDirectory _temp = new();

        public void Dispose() => _temp.Dispose();

        private static BackupFile File_(BackupReason reason, DateTimeOffset created, string? name = null) =>
            new(name ?? "/backups/" + BackupNaming.FileName("workspace", created, reason), created, reason, 1);

        private static HashSet<string> Names(IEnumerable<BackupFile> files) =>
            files.Select(f => Path.GetFileName(f.Path)).ToHashSet(StringComparer.Ordinal);

        [Fact]
        public void ManualBackups_AreAlwaysKept_HoweverOldOrNumerous()
        {
            var manual = Enumerable.Range(1, 20).Select(i => File_(BackupReason.Manual, Now.AddDays(-100 * i))).ToList();

            var plan = BackupRetention.Plan(manual, Now);

            Assert.Equal(20, plan.Keep.Count);
            Assert.Empty(plan.Delete);
        }

        [Fact]
        public void OfTheNonManualBackups_TheNewestFiveAreKept()
        {
            var files = Enumerable.Range(0, 8)
                .Select(i => File_(i % 2 == 0 ? BackupReason.Scheduled : BackupReason.Premigration, Now.AddDays(-40 - i)))
                .ToList();

            var plan = BackupRetention.Plan(files, Now);

            // all are older than 30 days, so only the newest five survive
            Assert.Equal(5, plan.Keep.Count);
            Assert.Equal(files.Take(5).Select(f => f.Path), plan.Keep.OrderByDescending(f => f.CreatedUtc).Select(f => f.Path));
            Assert.Equal(3, plan.Delete.Count);
        }

        [Fact]
        public void WithThreeBackups_AllAreKept()
        {
            var files = new[]
            {
                File_(BackupReason.Scheduled, Now.AddDays(-1)),
                File_(BackupReason.Scheduled, Now.AddDays(-2)),
                File_(BackupReason.Premigration, Now.AddDays(-90)),
            };

            var plan = BackupRetention.Plan(files, Now);

            Assert.Equal(3, plan.Keep.Count);
            Assert.Empty(plan.Delete);
        }

        [Fact]
        public void APremigrationBackupAtTheWindowEdge_IsKept_AndOneSecondOlderIsDropped()
        {
            // six newer scheduled backups fill the "newest five" so only the window can save a premigration file
            var newer = Enumerable.Range(1, 6).Select(i => File_(BackupReason.Scheduled, Now.AddHours(-i))).ToList();
            var atEdge = File_(BackupReason.Premigration, Now.AddDays(-30));
            var pastEdge = File_(BackupReason.Premigration, Now.AddDays(-30).AddSeconds(-1));

            var plan = BackupRetention.Plan([.. newer, atEdge, pastEdge], Now);

            Assert.Contains(atEdge, plan.Keep);
            Assert.Contains(pastEdge, plan.Delete);
        }

        [Fact]
        public void APremigrationBackupPastTheWindow_IsKept_WhenItIsWithinTheNewestFive()
        {
            var old = File_(BackupReason.Premigration, Now.AddDays(-200));

            var plan = BackupRetention.Plan([old], Now);

            Assert.Contains(old, plan.Keep);
        }

        [Fact]
        public void ARecentScheduledBackup_BeyondTheNewestFive_IsDeleted()
        {
            var files = Enumerable.Range(1, 6).Select(i => File_(BackupReason.Scheduled, Now.AddHours(-i))).ToList();

            var plan = BackupRetention.Plan(files, Now);

            var deleted = Assert.Single(plan.Delete);
            Assert.Equal(Now.AddHours(-6), deleted.CreatedUtc);
        }

        [Fact]
        public void EqualCreationTimes_AreOrderedByFileName_SoThePlanIsDeterministic()
        {
            // six files, one instant: the lexically first five (by name) must win, in any input order
            var instant = Now.AddDays(-1);
            var files = "fedcba".Select(c => File_(BackupReason.Scheduled, instant, $"/backups/workspace-{c}.db")).ToList();

            var forward = BackupRetention.Plan(files, Now);
            var reversed = BackupRetention.Plan(Enumerable.Reverse(files).ToList(), Now);

            Assert.Equal(new HashSet<string> { "workspace-a.db", "workspace-b.db", "workspace-c.db", "workspace-d.db", "workspace-e.db" }, Names(forward.Keep));
            Assert.Equal(new HashSet<string> { "workspace-f.db" }, Names(forward.Delete));
            Assert.Equal(Names(forward.Keep), Names(reversed.Keep));
            Assert.Equal(Names(forward.Delete), Names(reversed.Delete));
        }

        [Fact]
        public void TheDefaultPolicy_IsFiveAndThirtyDays()
        {
            Assert.Equal(5, RetentionPolicy.Default.KeepNewest);
            Assert.Equal(TimeSpan.FromDays(30), RetentionPolicy.Default.PremigrationWindow);
        }

        [Fact]
        public void ACustomPolicy_ChangesTheNumbers()
        {
            var files = Enumerable.Range(1, 4).Select(i => File_(BackupReason.Scheduled, Now.AddHours(-i))).ToList();

            var plan = BackupRetention.Plan(files, Now, new RetentionPolicy(2, TimeSpan.FromDays(1)));

            Assert.Equal(2, plan.Keep.Count);
            Assert.Equal(2, plan.Delete.Count);
        }

        [Theory]
        [InlineData("workspace-2026-bad.db")]
        [InlineData("../workspace-20260101T000000Z-manual.db")]
        [InlineData("sub/workspace-20260101T000000Z-manual.db")]
        [InlineData("deploy-20260101T000000Z-manual.db")]
        [InlineData("workspace-20260101T000000Z-manual.db.tmp")]
        [InlineData("workspace-20260101T000000Z-manual.db.manifest.json")]
        [InlineData("workspace-20260101T000000Z-weekly.db")]
        [InlineData("workspace-20260101T000000Z-Manual.db")]
        [InlineData("workspace-20260101T000000Z-manual.db\n")]
        [InlineData("workspace-20261301T000000Z-manual.db")]
        [InlineData("workspace-٢٠٢٦٠١٠١T000000Z-manual.db")]
        [InlineData("xworkspace-20260101T000000Z-manual.db")]
        public void NamesThatAreNotOurs_NeverParse(string name)
        {
            Assert.False(BackupNaming.TryParse(name, "workspace", out _, out _));
        }

        [Fact]
        public void AStemWithRegexCharacters_IsMatchedLiterally()
        {
            Assert.True(BackupNaming.TryParse("a.b-20260101T000000Z-manual.db", "a.b", out _, out _));
            Assert.False(BackupNaming.TryParse("axb-20260101T000000Z-manual.db", "a.b", out _, out _));
        }

        [Fact]
        public void ANameRoundTripsThroughTheFormat()
        {
            foreach (var reason in Enum.GetValues<BackupReason>())
            {
                var name = BackupNaming.FileName("workspace", Now, reason);
                Assert.True(BackupNaming.TryParse(name, "workspace", out var created, out var parsed));
                Assert.Equal(Now, created);
                Assert.Equal(reason, parsed);
            }
        }

        [Fact]
        public void List_NeverReturnsNamesThatDoNotMatch()
        {
            var dir = _temp.Path("backups");
            Directory.CreateDirectory(dir);
            var good = "workspace-20260101T000000Z-manual.db";
            foreach (var name in new[]
            {
                good, "workspace-2026-bad.db", "workspace-20260101T000000Z-manual.db.tmp",
                "workspace-20260101T000000Z-manual.db.manifest.json", "deploy-20260101T000000Z-manual.db", "notes.txt",
            })
                File.WriteAllText(Path.Combine(dir, name), "x");
            Directory.CreateDirectory(Path.Combine(dir, "workspace-20260102T000000Z-manual.db"));   // a directory with a matching name

            var listed = WorkspaceBackup.List(dir);

            Assert.Equal(good, Path.GetFileName(Assert.Single(listed).Path));
        }

        [Fact]
        public async Task PruneAsync_RemovesDbAndManifest_AndLeavesKeptAndUnrelatedFilesAlone()
        {
            var dir = _temp.Path("backups");
            Directory.CreateDirectory(dir);
            var manual = Seed(dir, BackupReason.Manual, Now.AddDays(-400));
            var scheduled = Enumerable.Range(1, 7).Select(i => Seed(dir, BackupReason.Scheduled, Now.AddHours(-i))).ToList();
            var unrelated = Path.Combine(dir, "notes.txt");
            File.WriteAllText(unrelated, "keep me");
            var otherStem = Path.Combine(dir, "deploy-20200101T000000Z-scheduled.db");
            File.WriteAllText(otherStem, "deploy");
            var outside = _temp.Path("backups-elsewhere");
            Directory.CreateDirectory(outside);
            var outsideFile = Seed(outside, BackupReason.Scheduled, Now.AddDays(-999));

            var deleted = await WorkspaceBackup.PruneAsync(dir, Now);

            Assert.Equal(2, deleted.Count);
            foreach (var gone in scheduled.Skip(5))
            {
                Assert.False(File.Exists(gone));
                Assert.False(File.Exists(gone + ".manifest.json"));
            }
            foreach (var kept in scheduled.Take(5).Append(manual))
            {
                Assert.True(File.Exists(kept));
                Assert.True(File.Exists(kept + ".manifest.json"));
            }
            Assert.True(File.Exists(unrelated));
            Assert.True(File.Exists(otherStem));
            Assert.True(File.Exists(outsideFile));
            Assert.True(File.Exists(outsideFile + ".manifest.json"));
        }

        [Fact]
        public async Task PruneAsync_OnAMissingDirectory_DeletesNothing()
        {
            Assert.Empty(await WorkspaceBackup.PruneAsync(_temp.Path("never-created"), Now));
        }

        [Fact]
        public async Task PruneAsync_ReportsAFileThatCannotBeRemoved_AndKeepsGoing()
        {
            if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;   // relies on a read-only directory
            var dir = _temp.Path("backups");
            Directory.CreateDirectory(dir);
            var files = Enumerable.Range(1, 6).Select(i => Seed(dir, BackupReason.Scheduled, Now.AddHours(-i))).ToList();
            var problems = new List<string>();

            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);   // deleting entries now fails
            try
            {
                var deleted = await WorkspaceBackup.PruneAsync(dir, Now, onProblem: problems.Add);

                Assert.Empty(deleted);
                Assert.NotEmpty(problems);
                Assert.True(File.Exists(files[5]));
            }
            finally
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        private static string Seed(string dir, BackupReason reason, DateTimeOffset created)
        {
            var path = Path.Combine(dir, BackupNaming.FileName("workspace", created, reason));
            File.WriteAllText(path, "db");
            File.WriteAllText(path + ".manifest.json", "{}");
            return path;
        }
    }
}
