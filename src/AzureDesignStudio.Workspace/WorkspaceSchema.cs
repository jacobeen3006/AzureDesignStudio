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
    bool Created, IReadOnlyList<string> AppliedMigrations, string JournalMode, string? PremigrationBackupPath = null);

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

        IReadOnlyList<string> pending;
        using (var db = new WorkspaceDbContext(WorkspaceConnection.WriterOptions(databasePath)))
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(options.MigrateTimeout);
            try
            {
                pending = (await db.Database.GetPendingMigrationsAsync(cts.Token).ConfigureAwait(false)).ToList();
                await db.Database.MigrateAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new WorkspaceUpgradeException(
                    $"The workspace upgrade did not finish within {options.MigrateTimeout.TotalSeconds:0} seconds. " +
                    $"An earlier upgrade may have been interrupted and left its lock in the table {LockTable}. " +
                    $"Close the app first, then run this statement against the database and start again: " +
                    $"DELETE FROM \"{LockTable}\"; " +
                    $"Database file: {databasePath}");
            }
            catch (DbException e)
            {
                throw new WorkspaceUpgradeException(
                    "The workspace upgrade failed and was rolled back (SQLite schema changes are transactional), " +
                    $"so the database is as it was before. {e.Message} Database file: {databasePath}", e);
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

        return new WorkspaceUpgradeResult(!existed, pending, journalMode);
    }

    // Unpooled, so the raw step neither keeps the file open nor leaves a pooled handle behind.
    private static SqliteConnection OpenRaw(string databasePath) =>
        new(new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString());
}
