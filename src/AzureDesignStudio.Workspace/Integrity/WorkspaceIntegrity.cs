using Microsoft.Data.Sqlite;

namespace AzureDesignStudio.Workspace;

/// <summary>How thorough a check is: <c>quick_check</c> (fast, no index/UNIQUE cross-checks) or <c>integrity_check</c>.</summary>
public enum IntegrityDepth
{
    Quick,
    Full,
}

/// <summary>How the file is opened for the check.</summary>
public enum IntegrityAccess
{
    /// <summary>
    /// The live workspace: opened read-write (never creating) so a leftover write-ahead log recovers.
    /// A read-only connection cannot run recovery, so opening the live file read-only after a crash
    /// would misreport a healthy file.
    /// </summary>
    LiveFile,

    /// <summary>A backup or restore candidate: opened read-only so the check can never alter it.</summary>
    Snapshot,
}

/// <summary>What a check concluded about a file.</summary>
public enum IntegrityOutcome
{
    Ok,

    /// <summary>The file content is damaged (or is not a database at all).</summary>
    Corrupt,

    /// <summary>The check could not run for a reason that says nothing about the content (lock, permission, IO).</summary>
    Inconclusive,

    /// <summary>There is no file at the path.</summary>
    Missing,
}

/// <summary>The outcome plus, for anything but <see cref="IntegrityOutcome.Ok"/>, the lines that explain it.</summary>
public sealed record IntegrityResult(IntegrityOutcome Outcome, IReadOnlyList<string> Problems)
{
    internal static IntegrityResult Ok { get; } = new(IntegrityOutcome.Ok, []);
}

/// <summary>
/// Tells corruption from contention. Runs <c>PRAGMA quick_check</c> / <c>integrity_check</c> on a raw,
/// unpooled connection and classifies the outcome exactly: a truncated file or destroyed header is
/// <see cref="IntegrityOutcome.Corrupt"/>; a zero-length main file next to a non-empty write-ahead log is
/// corrupt, while a zero-length file without one is a valid empty database; busy, locked, permission,
/// IO, full-disk and out-of-memory errors, and every other SQLite error code, are <see cref="IntegrityOutcome.Inconclusive"/>.
/// <para>
/// This deliberately narrows the CONTEXT entry "treat any exception from quick_check as corruption":
/// a second process holding the file must never cause a healthy workspace to be quarantined. Only
/// exceptions that say something about the file's content (SQLITE_CORRUPT, SQLITE_FORMAT, SQLITE_NOTADB)
/// are corruption; any other code is inconclusive.
/// </para>
/// <para>
/// Known blind spot: <c>quick_check</c> does not verify UNIQUE constraints or that index content matches
/// table content, and damage in the middle of the data can pass it. Restore verification uses
/// <see cref="IntegrityDepth.Full"/>.
/// </para>
/// </summary>
public static class WorkspaceIntegrity
{
    public static IntegrityResult Check(
        string path, IntegrityDepth depth = IntegrityDepth.Quick, IntegrityAccess access = IntegrityAccess.LiveFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var full = Path.GetFullPath(path);

        try
        {
            var file = new FileInfo(full);
            if (!file.Exists) return new IntegrityResult(IntegrityOutcome.Missing, [$"No file at {full}."]);

            if (file.Length == 0)
            {
                var wal = new FileInfo(full + "-wal");
                return wal.Exists && wal.Length > 0
                    ? new IntegrityResult(IntegrityOutcome.Corrupt, ["zero-length database with a write-ahead log"])
                    : IntegrityResult.Ok;   // a valid empty database
            }

            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = full,
                Mode = access == IntegrityAccess.LiveFile ? SqliteOpenMode.ReadWrite : SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = depth == IntegrityDepth.Full ? "PRAGMA integrity_check" : "PRAGMA quick_check";
            var lines = new List<string>();
            using (var reader = command.ExecuteReader())
                while (reader.Read()) lines.Add(reader.GetString(0));

            return lines is [var only] && string.Equals(only, "ok", StringComparison.Ordinal)
                ? IntegrityResult.Ok
                : new IntegrityResult(IntegrityOutcome.Corrupt, lines.Count == 0 ? ["the check returned no rows"] : lines);
        }
        catch (SqliteException e)
        {
            var problem = $"SQLite error {e.SqliteErrorCode}: {e.Message}";
            return new IntegrityResult(
                IntegrityCodes.IsCorrupt(e.SqliteErrorCode) ? IntegrityOutcome.Corrupt : IntegrityOutcome.Inconclusive,
                [problem]);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new IntegrityResult(IntegrityOutcome.Inconclusive, [$"{e.GetType().Name}: {e.Message}"]);
        }
    }
}

/// <summary>
/// Primary SQLite result codes. An allow-list: only the codes that SQLite documents as "the file content is
/// damaged" count as corruption; every other code (busy, locked, protocol, interrupt, abort, misuse, auth,
/// schema, a plain SQL error, a code this list has never heard of) says nothing about the content and is
/// inconclusive, so startup stops with "nothing was moved" instead of moving a possibly healthy workspace.
/// </summary>
internal static class IntegrityCodes
{
    /// <summary>corrupt 11, format 24, notadb 26.</summary>
    public static bool IsCorrupt(int primaryCode) => primaryCode is 11 or 24 or 26;

    public static bool IsInconclusive(int primaryCode) => !IsCorrupt(primaryCode);
}
