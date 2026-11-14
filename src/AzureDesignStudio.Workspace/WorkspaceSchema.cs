using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureDesignStudio.Workspace;

/// <summary>What <see cref="WorkspaceSchema.UpgradeAsync"/> did to the database file.</summary>
/// <param name="Created">The file did not exist (or was empty), so this run created it.</param>
/// <param name="AppliedMigrations">Ids of the migrations this run applied, oldest first.</param>
/// <param name="JournalMode">The journal mode SQLite reported after the upgrade (expected <c>wal</c>).</param>
/// <param name="PremigrationBackupPath">The backup written before the schema change, or null when none was needed.</param>
public sealed record WorkspaceUpgradeResult(
    bool Created, IReadOnlyList<string> AppliedMigrations, string JournalMode, string? PremigrationBackupPath = null)
{
    /// <summary>Duplicate rows the unique-index guard removed before the migration (empty when the data was clean).</summary>
    public IReadOnlyList<UniqueIndexRepair> UniqueIndexRepairs { get; init; } = [];

    /// <summary>
    /// What to tell the user at launch when the upgrade removed duplicate rows (null when it removed none):
    /// the table, how many rows, which one was kept, where the report is and which backup still holds them.
    /// </summary>
    public string? RemovedRowsMessage => WorkspaceSchema.DescribeRemovedRows(UniqueIndexRepairs, PremigrationBackupPath);
}

/// <summary>The workspace database could not be brought to the current schema.</summary>
public sealed class WorkspaceUpgradeException : Exception
{
    public WorkspaceUpgradeException(string message) : base(message) { }

    public WorkspaceUpgradeException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Knobs of the upgrade; tests shorten the migration bound.</summary>
internal sealed class WorkspaceUpgradeOptions
{
    /// <summary>How long <c>MigrateAsync</c> may run before the upgrade is reported as stuck.</summary>
    public TimeSpan MigrateTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Where the premigration backup goes; null means a <c>backups</c> folder beside the database.</summary>
    public string? BackupsDirectory { get; init; }

    /// <summary>The clock that stamps the premigration backup.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>
/// Brings a workspace database file to the current schema: legacy adoption (a file that predates
/// migrations), a bounded <c>MigrateAsync</c>, and the WAL assertion. <see cref="WorkspaceDb"/>
/// calls it on construction, so every existing caller keeps getting "open and make current".
/// </summary>
internal static class WorkspaceSchema
{
    private const string LockTable = "__EFMigrationsLock";
    private const string HistoryTable = "__EFMigrationsHistory";

    public static async Task<WorkspaceUpgradeResult> UpgradeAsync(
        string databasePath, WorkspaceUpgradeOptions options, CancellationToken ct)
    {
        var existed = File.Exists(databasePath) && new FileInfo(databasePath).Length > 0;

        // What is pending decides whether an existing file needs a backup: a legacy file has no history table,
        // so every migration is pending; a brand-new file also reports them all, but then there is nothing to lose.
        using var db = new WorkspaceDbContext(WorkspaceConnection.WriterOptions(databasePath));
        IReadOnlyList<string> pending;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(options.MigrateTimeout);
            pending = (await db.Database.GetPendingMigrationsAsync(cts.Token).ConfigureAwait(false)).ToList();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw StuckUpgrade(databasePath, options);
        }
        catch (DbException e)
        {
            throw new WorkspaceUpgradeException(
                $"The workspace database could not be read to plan the upgrade ({e.Message}). " +
                $"Nothing was changed. Database file: {databasePath}", e);
        }

        // The backup comes before anything alters the file (legacy adoption included). If it cannot be
        // written the upgrade is not attempted, so the live file is exactly as it was.
        string? premigrationBackup = null;
        IReadOnlyList<UniqueIndexRepair> uniqueIndexRepairs = [];
        if (existed && pending.Count > 0)
        {
            var backupsDirectory = options.BackupsDirectory
                ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "backups");
            try
            {
                premigrationBackup = (await WorkspaceBackup.CreateAsync(
                    databasePath, backupsDirectory, BackupReason.Premigration, options.Clock.GetUtcNow(), "workspace", ct)
                    .ConfigureAwait(false)).Path;
            }
            catch (Exception e) when (e is not OperationCanceledException && (e is IOException or UnauthorizedAccessException
                or SqliteException or ArgumentException or NotSupportedException))
            {
                throw new WorkspaceUpgradeException(
                    "The workspace needs an upgrade but its backup could not be written, so the upgrade was not attempted " +
                    $"and the workspace file is unchanged. Reason: {e.Message} Backups folder: {backupsDirectory} " +
                    $"Database file: {databasePath}", e);
            }
        }

        // Rows that would violate a unique index the baseline creates are removed (newest kept, the rest reported)
        // only after the backup above exists; one transaction, so a failure leaves every row in place.
        if (existed && pending.Count > 0)
        {
            try
            {
                using var raw = OpenRaw(databasePath);
                await raw.OpenAsync(ct).ConfigureAwait(false);
                uniqueIndexRepairs = UniqueIndexGuard.Repair(
                    raw, UniqueIndexGuard.Specs, Path.GetDirectoryName(premigrationBackup!)!, options.Clock.GetUtcNow(), premigrationBackup!);
            }
            catch (SqliteException e)
            {
                throw new WorkspaceUpgradeException(
                    $"Duplicate rows that the new unique indexes would reject could not be cleaned up ({e.Message}). " +
                    $"Nothing was removed. The premigration backup is {premigrationBackup} Database file: {databasePath}", e);
            }
            catch (IOException e)
            {
                throw new WorkspaceUpgradeException(
                    $"The report of removed duplicate rows could not be written ({e.Message}), so no row was removed. " +
                    $"The premigration backup is {premigrationBackup} Database file: {databasePath}", e);
            }
        }

        // A file that predates migrations: add the columns the guarded baseline cannot add.
        if (existed)
        {
            try
            {
                using var raw = OpenRaw(databasePath);
                await raw.OpenAsync(ct).ConfigureAwait(false);
                if (!LegacyAdoption.HasTable(raw, HistoryTable) && LegacyAdoption.HasTable(raw, "Scans"))
                    LegacyAdoption.AddMissingScanColumns(raw);
            }
            catch (SqliteException e)
            {
                throw new WorkspaceUpgradeException(
                    $"The workspace database could not be prepared for the upgrade ({e.Message}). " +
                    $"Nothing was changed beyond the failed statement. File: {databasePath}", e);
            }
        }

        using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            cts.CancelAfter(options.MigrateTimeout);
            try
            {
                await db.Database.MigrateAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw StuckUpgrade(databasePath, options);
            }
            catch (DbException e)
            {
                // The duplicate-row cleanup above is committed on its own, before the migration: it is not rolled back.
                var removed = uniqueIndexRepairs.Count == 0
                    ? string.Empty
                    : " The duplicate rows removed just before the upgrade stay removed; " + DescribeRemovedRows(uniqueIndexRepairs, premigrationBackup);
                throw new WorkspaceUpgradeException(
                    "The workspace upgrade failed and was rolled back (SQLite schema changes are transactional), " +
                    $"so the schema is as it was before. {e.Message}{removed} Database file: {databasePath}", e);
            }
        }

        string journalMode;
        try
        {
            using var raw = OpenRaw(databasePath);
            await raw.OpenAsync(ct).ConfigureAwait(false);
            using var wal = raw.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            journalMode = Convert.ToString(await wal.ExecuteScalarAsync(ct).ConfigureAwait(false)) ?? string.Empty;
        }
        catch (SqliteException e)
        {
            throw new WorkspaceUpgradeException(
                $"The workspace database was upgraded but write-ahead logging could not be enabled ({e.Message}). " +
                $"File: {databasePath}", e);
        }

        if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            throw new WorkspaceUpgradeException(
                $"The workspace database stayed in journal mode '{journalMode}' instead of 'wal'. File: {databasePath}");

        return new WorkspaceUpgradeResult(!existed, pending, journalMode, premigrationBackup)
        {
            UniqueIndexRepairs = uniqueIndexRepairs,
        };
    }

    internal static string? DescribeRemovedRows(IReadOnlyList<UniqueIndexRepair> repairs, string? premigrationBackupPath)
    {
        if (repairs.Count == 0) return null;

        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var message = new System.Text.StringBuilder();
        message.AppendLine("The workspace upgrade removed duplicate rows that the new unique indexes would reject. "
            + "For each duplicated key the most recently saved row was kept:");
        foreach (var repair in repairs)
            message.AppendLine(string.Create(inv, $"  {repair.Table}: {repair.RowsRemoved} row(s) removed from {repair.DuplicateGroups} duplicated key(s)"));
        message.AppendLine($"Every removed row is in the report: {repairs[0].ReportPath}");
        message.Append(premigrationBackupPath is null
            ? "A premigration backup holds the rows too."
            : $"The premigration backup {premigrationBackupPath} still holds all of them.");
        return message.ToString();
    }

    private static WorkspaceUpgradeException StuckUpgrade(string databasePath, WorkspaceUpgradeOptions options) =>
        new($"The workspace upgrade did not finish within {options.MigrateTimeout.TotalSeconds:0} seconds. " +
            $"An earlier upgrade may have been interrupted and left its lock in the table {LockTable}. " +
            $"Close the app first, then run this statement against the database and start again: " +
            $"DELETE FROM \"{LockTable}\"; " +
            $"Database file: {databasePath}");

    // Unpooled, so the raw step neither keeps the file open nor leaves a pooled handle behind.
    private static SqliteConnection OpenRaw(string databasePath) =>
        new(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
}
