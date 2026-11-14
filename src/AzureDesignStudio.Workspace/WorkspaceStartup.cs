using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;

namespace AzureDesignStudio.Workspace;

/// <summary>How a workspace came up at startup.</summary>
public enum WorkspaceStartupOutcome
{
    /// <summary>There was no workspace (or an empty file); a new one was created.</summary>
    Fresh,

    /// <summary>The workspace was healthy and already current.</summary>
    Opened,

    /// <summary>The workspace was healthy and one or more migrations were applied (after a premigration backup).</summary>
    Upgraded,

    /// <summary>The workspace was corrupt: it was moved aside intact and a new empty one was created.</summary>
    RecoveredFromCorruption,
}

/// <summary>
/// Where startup and the paths it should use are; <see cref="BackupsDirectory"/> is where premigration
/// backups go and where the notice looks for backups to offer.
/// </summary>
public sealed record WorkspaceStartupOptions(string WorkspacePath, string BackupsDirectory, TimeProvider? Clock = null)
{
    /// <summary>Test seam: replaces the startup integrity check.</summary>
    internal Func<string, IntegrityResult>? Integrity { get; init; }
}

/// <summary>What the user must be told after a recovery: the quarantine path and the backups that exist.</summary>
public sealed record WorkspaceNotice(string Message, string QuarantinePath, IReadOnlyList<BackupFile> Backups);

/// <summary>What startup did, for the host to surface.</summary>
public sealed record WorkspaceStartupReport(
    WorkspaceStartupOutcome Outcome, WorkspaceNotice? Notice, WorkspaceUpgradeResult Upgrade);

/// <summary>The opened workspace plus the report of how it was opened.</summary>
public sealed record WorkspaceStartupResult(WorkspaceDb Db, WorkspaceStartupReport Report);

/// <summary>
/// The workspace startup state machine: a staged integrity check, quarantine of a corrupt file (moved
/// aside, never deleted) with a fresh workspace created in its place, and the schema upgrade, whose
/// premigration backup protects every existing database.
/// </summary>
public static class WorkspaceStartup
{
    private const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";
    private const int NoticeBackupLimit = 5;

    /// <summary>Blocking form of <see cref="OpenAsync"/>; the thread-pool hop keeps a UI synchronization context from deadlocking.</summary>
    public static WorkspaceStartupResult Open(WorkspaceStartupOptions options) =>
        Task.Run(() => OpenAsync(options, CancellationToken.None)).GetAwaiter().GetResult();

    public static async Task<WorkspaceStartupResult> OpenAsync(WorkspaceStartupOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.WorkspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.BackupsDirectory);
        var path = Path.GetFullPath(options.WorkspacePath);
        var backups = Path.GetFullPath(options.BackupsDirectory);
        var clock = options.Clock ?? TimeProvider.System;

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // The file work is synchronous; run it off the caller's thread so an async caller is not blocked by it.
        var check = await Task.Run(
            () => options.Integrity is { } injected
                ? injected(path)
                : WorkspaceIntegrity.Check(path, IntegrityDepth.Quick, IntegrityAccess.LiveFile),
            ct).ConfigureAwait(false);

        string? quarantined = null;
        switch (check.Outcome)
        {
            case IntegrityOutcome.Inconclusive:
                throw new WorkspaceUpgradeException(
                    $"The workspace could not be inspected ({string.Join("; ", check.Problems)}). " +
                    "Another copy of the app may be running, or there is a permissions or I/O problem. " +
                    $"Nothing was moved or changed. File: {path}");
            case IntegrityOutcome.Corrupt:
                quarantined = await Task.Run(() => Quarantine(path, clock), ct).ConfigureAwait(false);
                break;
        }

        var upgrade = await WorkspaceSchema.UpgradeAsync(
            path, new WorkspaceUpgradeOptions { BackupsDirectory = backups, Clock = clock }, ct).ConfigureAwait(false);

        var outcome = quarantined is not null ? WorkspaceStartupOutcome.RecoveredFromCorruption
            : upgrade.Created ? WorkspaceStartupOutcome.Fresh
            : upgrade.AppliedMigrations.Count > 0 ? WorkspaceStartupOutcome.Upgraded
            : WorkspaceStartupOutcome.Opened;

        var notice = quarantined is null ? null : BuildNotice(quarantined, path, backups);
        return new WorkspaceStartupResult(
            new WorkspaceDb(path, alreadyUpgraded: true),
            new WorkspaceStartupReport(outcome, notice, upgrade));
    }

    /// <summary>
    /// Moves the main file and any <c>-wal</c>/<c>-shm</c> to <c>workspace.corrupt-&lt;UTC&gt;.db[-wal|-shm]</c> beside
    /// the workspace. Nothing is deleted or overwritten; a failed move is undone and reported.
    /// </summary>
    private static string Quarantine(string path, TimeProvider clock)
    {
        SqliteConnection.ClearAllPools();   // release any pooled handle that would hold the files open

        var directory = Path.GetDirectoryName(path)!;
        var stamp = clock.GetUtcNow().UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var stem = Path.Combine(directory, "workspace.corrupt-" + stamp);
        for (var n = 1; AnyExists(stem); n++)
            stem = Path.Combine(directory, $"workspace.corrupt-{stamp}-{n}");

        var moves = new List<(string From, string To)> { (path, stem + ".db") };
        if (File.Exists(path + "-wal")) moves.Add((path + "-wal", stem + ".db-wal"));
        if (File.Exists(path + "-shm")) moves.Add((path + "-shm", stem + ".db-shm"));

        var moved = new List<(string From, string To)>();
        try
        {
            foreach (var move in moves)
            {
                File.Move(move.From, move.To, overwrite: false);
                moved.Add(move);
            }

            // A move keeps the original permissions; the quarantined file holds the user's inventory and designs.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(stem + ".db", UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            foreach (var (from, to) in moved)
            {
                try { File.Move(to, from, overwrite: false); } catch (Exception undo) when (undo is IOException or UnauthorizedAccessException) { }
            }

            throw new WorkspaceUpgradeException(
                $"The workspace looks damaged but could not be moved aside ({e.Message}). " +
                $"Nothing was deleted or replaced. File: {path}", e);
        }

        return stem + ".db";
    }

    private static bool AnyExists(string stem) =>
        File.Exists(stem + ".db") || File.Exists(stem + ".db-wal") || File.Exists(stem + ".db-shm");

    private static WorkspaceNotice BuildNotice(string quarantinePath, string workspacePath, string backupsDirectory)
    {
        var backups = WorkspaceBackup.List(backupsDirectory);
        var message = new StringBuilder();
        message.AppendLine("The workspace database was damaged and was not opened.");
        message.AppendLine($"The damaged file was kept, unchanged, at: {quarantinePath}");
        message.AppendLine($"A new empty workspace was created at: {workspacePath}");
        if (backups.Count == 0)
        {
            message.Append("No backups are available.");
        }
        else
        {
            message.AppendLine($"Backups available in {backupsDirectory}:");
            foreach (var backup in backups.Take(NoticeBackupLimit))
            {
                message.AppendLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {Path.GetFileName(backup.Path)} ({backup.Reason}, {backup.CreatedUtc:yyyy-MM-dd HH:mm:ss} UTC, {backup.SizeBytes:N0} bytes)"));
            }

            if (backups.Count > NoticeBackupLimit)
                message.AppendLine($"  ... and {backups.Count - NoticeBackupLimit} more.");
        }

        return new WorkspaceNotice(message.ToString().TrimEnd(), quarantinePath, backups);
    }
}
