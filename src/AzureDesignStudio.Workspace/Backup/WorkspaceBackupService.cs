using Microsoft.Data.Sqlite;

namespace AzureDesignStudio.Workspace;

/// <summary>
/// What the Desktop host calls for backups: a manual backup on demand (never pruned), the scheduled backup
/// (once at startup when the newest scheduled-or-premigration backup is more than 24 hours old, then every
/// 24 hours, each followed by pruning) and the staged restore. It holds no connection between calls and
/// has no UI or MAUI dependency: the host only starts the loop and shows the results.
/// </summary>
public sealed class WorkspaceBackupService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    private const int CheckpointBusyTimeoutMilliseconds = 250;

    private readonly string _workspacePath;
    private readonly string _appDataDirectory;
    private readonly TimeProvider _clock;
    private readonly Action<Exception>? _onError;

    public WorkspaceBackupService(
        string workspacePath, string backupsDirectory, TimeProvider? clock = null, Action<Exception>? onError = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupsDirectory);
        _workspacePath = Path.GetFullPath(workspacePath);
        _appDataDirectory = Path.GetDirectoryName(_workspacePath) ?? ".";
        BackupsDirectory = Path.GetFullPath(backupsDirectory);
        _clock = clock ?? TimeProvider.System;
        _onError = onError;
    }

    public string BackupsDirectory { get; }

    /// <summary>A <see cref="BackupReason.Manual"/> backup of the workspace; retention never deletes it.</summary>
    public Task<BackupResult> CreateManualAsync(CancellationToken ct = default) =>
        WorkspaceBackup.CreateAsync(_workspacePath, BackupsDirectory, BackupReason.Manual, _clock.GetUtcNow(), "workspace", ct);

    /// <summary>The workspace backups (not the deploy ones that share the folder), newest first.</summary>
    public IReadOnlyList<BackupFile> List() => WorkspaceBackup.List(BackupsDirectory, "workspace");

    /// <summary>
    /// Takes a <see cref="BackupReason.Scheduled"/> backup when no scheduled or premigration backup is newer
    /// than 24 hours (a manual one does not count), prunes to the retention rule and tries to truncate the
    /// write-ahead log. Returns whether a backup was taken.
    /// </summary>
    public async Task<bool> RunScheduledIfDueAsync(CancellationToken ct = default)
    {
        var now = _clock.GetUtcNow();
        var newest = List()
            .Where(b => b.Reason is BackupReason.Scheduled or BackupReason.Premigration)
            .Select(b => (DateTimeOffset?)b.CreatedUtc)
            .Max();
        if (newest is { } last && now - last <= Interval) return false;

        await WorkspaceBackup.CreateAsync(_workspacePath, BackupsDirectory, BackupReason.Scheduled, now, "workspace", ct)
            .ConfigureAwait(false);
        await WorkspaceBackup.PruneAsync(BackupsDirectory, now, "workspace", ct: ct,
            onProblem: message => _onError?.Invoke(new IOException(message))).ConfigureAwait(false);
        TryCheckpoint();
        return true;
    }

    /// <summary>
    /// The host's background loop: one due check at once, then one every 24 hours until cancelled. A failing
    /// tick goes to the error callback and the loop carries on; cancellation ends it without an exception.
    /// </summary>
    public Task RunLoopAsync(CancellationToken ct) => RunLoopAsync(Interval, ct);

    // Tests pass a short period instead of waiting 24 real hours.
    internal async Task RunLoopAsync(TimeSpan period, CancellationToken ct)
    {
        try
        {
            await TickAsync(ct).ConfigureAwait(false);
            using var timer = new PeriodicTimer(period, _clock);
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                await TickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // normal shutdown
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        try
        {
            await RunScheduledIfDueAsync(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            _onError?.Invoke(e);
        }
    }

    /// <summary>Copies a chosen backup into the staging folder and verifies it; the restore is applied at the next launch.</summary>
    public Task<RestoreVerification> StageRestoreAsync(string pickedPath, CancellationToken ct = default) =>
        RestoreStaging.StageAsync(pickedPath, _appDataDirectory, ct);

    /// <summary>The restore waiting for the next launch, or null.</summary>
    public PendingRestore? PendingRestore => RestoreStaging.GetPending(_appDataDirectory);

    public void CancelPendingRestore() => RestoreStaging.CancelPending(_appDataDirectory);

    // Opportunistic: a busy checkpoint is expected while readers are active and must never fail a tick.
    private void TryCheckpoint()
    {
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = _workspacePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA busy_timeout={CheckpointBusyTimeoutMilliseconds}; PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            // contention or a permission problem: the next tick tries again
        }
    }
}
