using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AzureDesignStudio.Core.Tests.Support;
using AzureDesignStudio.Workspace;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AzureDesignStudio.Core.Tests
{
    /// <summary>
    /// The backup service the Desktop host starts: manual backups, the 24-hour schedule with pruning and the
    /// restore facade. Everything runs without MAUI and with an injected clock.
    /// </summary>
    public class WorkspaceBackupServiceTests : IDisposable
    {
        private static readonly DateTimeOffset T0 = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

        private readonly TempWorkspaceDirectory _temp = new();
        private readonly ManualTimeProvider _clock = new(T0);

        public void Dispose() => _temp.Dispose();

        private string WorkspacePath => _temp.Path("workspace.db");

        private string BackupsPath => _temp.Path("backups");

        private WorkspaceBackupService Service(Action<Exception>? onError = null, string? backups = null)
        {
            if (!File.Exists(WorkspacePath))
            {
                WorkspaceStartup.Open(new WorkspaceStartupOptions(WorkspacePath, BackupsPath, _clock));
                SqliteConnection.ClearAllPools();
            }

            return new WorkspaceBackupService(WorkspacePath, backups ?? BackupsPath, _clock, onError);
        }

        [Fact]
        public async Task ManualBackups_AreListed_AndSurviveAnyNumberOfScheduledPrunes()
        {
            var service = Service();
            var manual = await service.CreateManualAsync();
            Assert.Equal(BackupReason.Manual, BackupManifest.Read(manual.ManifestPath).Reason);
            Assert.Contains(service.List(), b => b.Path == manual.Path && b.Reason == BackupReason.Manual);

            for (var i = 0; i < 9; i++)
            {
                _clock.Advance(TimeSpan.FromHours(25));
                Assert.True(await service.RunScheduledIfDueAsync());
            }

            Assert.Contains(service.List(), b => b.Path == manual.Path);
            Assert.True(File.Exists(manual.Path));
            Assert.True(File.Exists(manual.ManifestPath));
        }

        [Fact]
        public async Task TheScheduledBackup_IsTakenWhenNoneExists_SkippedUnder24Hours_AndTakenAgainAfter()
        {
            var service = Service();

            Assert.True(await service.RunScheduledIfDueAsync());
            Assert.Equal(BackupReason.Scheduled, Assert.Single(service.List()).Reason);

            _clock.Advance(TimeSpan.FromHours(23) + TimeSpan.FromMinutes(59));
            Assert.False(await service.RunScheduledIfDueAsync());
            Assert.Single(service.List());

            _clock.Advance(TimeSpan.FromMinutes(2));
            Assert.True(await service.RunScheduledIfDueAsync());
            Assert.Equal(2, service.List().Count);
        }

        [Fact]
        public async Task APremigrationBackupUnder24HoursOld_CountsAsRecent()
        {
            var service = Service();
            await WorkspaceBackup.CreateAsync(WorkspacePath, BackupsPath, BackupReason.Premigration, T0.AddHours(-3));

            Assert.False(await service.RunScheduledIfDueAsync());
            Assert.Equal(BackupReason.Premigration, Assert.Single(service.List()).Reason);
        }

        [Fact]
        public async Task AManualBackupAlone_DoesNotMakeTheScheduleSkip()
        {
            var service = Service();
            await service.CreateManualAsync();

            Assert.True(await service.RunScheduledIfDueAsync());
        }

        [Fact]
        public async Task AfterSevenScheduledBackups_TheNewestFiveAreKept_PlusRecentPremigrationAndManualOnes()
        {
            var service = Service();
            var manual = await service.CreateManualAsync();
            var finalTime = T0.AddHours(6 * 25);
            var recentPremigration = await WorkspaceBackup.CreateAsync(
                WorkspacePath, BackupsPath, BackupReason.Premigration, finalTime.AddDays(-10));
            var oldPremigration = await WorkspaceBackup.CreateAsync(
                WorkspacePath, BackupsPath, BackupReason.Premigration, finalTime.AddDays(-40));

            for (var i = 0; i < 7; i++)
            {
                Assert.True(await service.RunScheduledIfDueAsync());
                _clock.Advance(TimeSpan.FromHours(25));
            }

            var files = service.List();
            Assert.Equal(5, files.Count(f => f.Reason == BackupReason.Scheduled));
            Assert.Contains(files, f => f.Path == manual.Path);
            Assert.Contains(files, f => f.Path == recentPremigration.Path);
            Assert.DoesNotContain(files, f => f.Path == oldPremigration.Path);
            Assert.False(File.Exists(oldPremigration.ManifestPath));
        }

        [Fact]
        public async Task TheLoop_RunsTheDueCheckAtOnce_ThenWaits_AndEndsQuietlyWhenCancelled()
        {
            var service = Service();
            using var cts = new CancellationTokenSource();

            var loop = service.RunLoopAsync(TimeSpan.FromHours(24), cts.Token);

            await WaitUntilAsync(() => service.List().Count == 1);
            await Task.Delay(100);
            Assert.Single(service.List());   // the next tick is 24 hours away

            cts.Cancel();
            await loop.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(loop.IsCompletedSuccessfully);
        }

        [Fact]
        public async Task AFailingTick_IsReported_AndDoesNotEndTheLoop()
        {
            var blocker = _temp.Path("not-a-folder");
            File.WriteAllText(blocker, "a file where the backups folder should be");
            var errors = new ConcurrentQueue<Exception>();
            var service = Service(errors.Enqueue, backups: blocker);
            using var cts = new CancellationTokenSource();

            var loop = service.RunLoopAsync(TimeSpan.FromMilliseconds(20), cts.Token);

            await WaitUntilAsync(() => errors.Count >= 3);   // the first run and at least two ticks that followed
            Assert.False(loop.IsCompleted);

            cts.Cancel();
            await loop.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(loop.IsCompletedSuccessfully);
        }

        [Fact]
        public async Task TheRestoreFacade_StagesCancelsAndReportsThePendingRestoreOfThisWorkspace()
        {
            var service = Service();
            var backup = await service.CreateManualAsync();
            Assert.Null(service.PendingRestore);

            var verdict = await service.StageRestoreAsync(backup.Path);
            Assert.True(verdict.Accepted, string.Join("; ", verdict.Reasons));
            Assert.Equal(Path.GetFileName(backup.Path), service.PendingRestore!.SourceName);
            Assert.True(File.Exists(_temp.Path("restore-staging/restore.pending.json")));

            service.CancelPendingRestore();
            Assert.Null(service.PendingRestore);
            Assert.False(Directory.Exists(_temp.Path("restore-staging")));
        }

        [Fact]
        public async Task List_ReadsTheWorkspaceStemOnly_NotTheDeployBackupsThatShareTheFolder()
        {
            var service = Service();
            await service.CreateManualAsync();
            await WorkspaceBackup.CreateAsync(WorkspacePath, BackupsPath, BackupReason.Manual, T0.AddMinutes(5), stem: "deploy");

            var listed = Assert.Single(service.List());
            Assert.StartsWith("workspace-", Path.GetFileName(listed.Path), StringComparison.Ordinal);
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (!condition())
            {
                Assert.True(DateTime.UtcNow < deadline, "the condition was not met in time");
                await Task.Delay(10);
            }
        }

        private sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
        {
            private DateTimeOffset _now = start;

            public override DateTimeOffset GetUtcNow() => _now;

            public void Advance(TimeSpan by) => _now += by;
        }
    }
}
