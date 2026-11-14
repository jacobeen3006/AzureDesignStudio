using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureDesignStudio.Workspace;

/// <summary>The verdict on a restore candidate. <see cref="Reasons"/> holds every rejection reason, or the notes of an accepted file.</summary>
public sealed record RestoreVerification(bool Accepted, IReadOnlyList<string> Reasons);

/// <summary>A verified candidate waiting for the next launch: the file name the user picked, when it was staged and the SHA-256 of the staged copy.</summary>
public sealed record PendingRestore(string SourceName, DateTimeOffset StagedUtc, string Sha256);

/// <summary>What applying a pending restore did; <see cref="PreRestorePath"/> is where the previous workspace went.</summary>
public sealed record RestoreOutcome(bool Applied, string SourceName, string? PreRestorePath, string? Problem);

/// <summary>
/// Restore as a two-step, never a hot swap: the chosen file is copied into a staging folder and verified
/// THERE (the picked path may be a temporary copy or change underneath), then a marker records its SHA-256.
/// At the next launch, before any workspace connection exists, <see cref="ApplyPendingAsync"/> re-hashes
/// the candidate and moves it into place; the previous workspace is moved aside to
/// <c>workspace.pre-restore-&lt;UTC&gt;.db</c>, never deleted. A restore candidate is untrusted input.
/// </summary>
public static class RestoreStaging
{
    private const string StagingFolderName = "restore-staging";
    private const string CandidateName = "candidate.db";
    private const string MarkerName = "restore.pending.json";
    private const string TimestampFormat = "yyyyMMdd'T'HHmmss'Z'";
    private static readonly byte[] SqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static string StagingDirectory(string appDataDirectory) =>
        Path.Combine(Path.GetFullPath(appDataDirectory), StagingFolderName);

    /// <summary>
    /// Verifies a candidate file in place: it exists and is not empty, starts with the SQLite header, passes
    /// <c>integrity_check</c>, holds no triggers, views, virtual tables or tables outside the workspace model,
    /// has only migration ids this build knows (a newer schema is refused; a file with no history table is a
    /// legacy database that the startup upgrade migrates forward) and, when a manifest accompanies it,
    /// matches the manifest's SHA-256 and row counts. Every reason is collected. Callers verify the staged
    /// copy, never the user's original.
    /// </summary>
    public static Task<RestoreVerification> VerifyAsync(string candidatePath, string? manifestPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        return Task.Run(() => Verify(Path.GetFullPath(candidatePath), manifestPath), ct);
    }

    private static RestoreVerification Verify(string candidate, string? manifestPath)
    {
        var reasons = new List<string>();
        if (!File.Exists(candidate))
            return new RestoreVerification(false, [$"The file does not exist: {candidate}"]);
        if (new FileInfo(candidate).Length == 0)
            return new RestoreVerification(false, ["The file is empty."]);
        if (!HasSqliteHeader(candidate))
            return new RestoreVerification(false, ["The file is not a SQLite database (its header does not match)."]);

        var integrity = WorkspaceIntegrity.Check(candidate, IntegrityDepth.Full, IntegrityAccess.Snapshot);
        if (integrity.Outcome != IntegrityOutcome.Ok)
        {
            reasons.Add($"The database failed its integrity check ({integrity.Outcome}): {string.Join("; ", integrity.Problems)}");
            return new RestoreVerification(false, reasons);
        }

        var counts = VerifySchema(candidate, reasons);
        VerifyManifest(candidate, manifestPath, counts, reasons, out var note);

        var accepted = reasons.Count == 0;
        if (accepted && note is not null) reasons.Add(note);
        return new RestoreVerification(accepted, reasons);
    }

    private static readonly string[] BookkeepingTables =
        ["__EFMigrationsHistory", "__EFMigrationsLock", "sqlite_sequence", "sqlite_stat1"];

    // Read once from the model and the compiled migrations: nothing here opens a database.
    private static readonly Lazy<(HashSet<string> Tables, HashSet<string> Migrations)> Known = new(() =>
    {
        using var context = new WorkspaceDbContextFactory().CreateDbContext([]);
        var tables = context.Model.GetEntityTypes()
            .Select(e => e.GetTableName())
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        tables.UnionWith(BookkeepingTables);
        return (tables, context.Database.GetMigrations().ToHashSet(StringComparer.Ordinal));
    });

    /// <summary>
    /// Reads the schema on a read-only, unpooled connection with <c>trusted_schema</c> off. Returns the row
    /// count of every table (only needed for the manifest comparison) or null when the schema was rejected.
    /// Names read from the file are shown in reasons and used as quoted identifiers for counting only.
    /// </summary>
    private static Dictionary<string, long>? VerifySchema(string candidate, List<string> reasons)
    {
        var (knownTables, knownMigrations) = Known.Value;
        try
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = candidate,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA trusted_schema=OFF";
                pragma.ExecuteNonQuery();
            }

            var tables = new List<string>();
            var rejected = false;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT type, name, sql FROM sqlite_master";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var type = reader.GetString(0);
                    var name = reader.GetString(1);
                    var sql = reader.IsDBNull(2) ? "" : reader.GetString(2);
                    switch (type)
                    {
                        case "trigger":
                            reasons.Add($"The database contains a trigger ({Safe(name)}); a workspace never has triggers.");
                            rejected = true;
                            break;
                        case "view":
                            reasons.Add($"The database contains a view ({Safe(name)}); a workspace never has views.");
                            rejected = true;
                            break;
                        case "table" when sql.StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase):
                            reasons.Add($"The database contains a virtual table ({Safe(name)}); a workspace never has virtual tables.");
                            rejected = true;
                            break;
                        case "table" when !knownTables.Contains(name):
                            reasons.Add($"The database contains a table this app does not know ({Safe(name)}).");
                            rejected = true;
                            break;
                        case "table":
                            tables.Add(name);
                            break;
                    }
                }
            }

            if (tables.Contains("__EFMigrationsHistory", StringComparer.Ordinal))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\"";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var id = reader.GetString(0);
                    if (knownMigrations.Contains(id)) continue;
                    reasons.Add($"The database comes from a newer version of the app (unknown migration {Safe(id)}); update the app to restore it.");
                    rejected = true;
                }
            }

            if (rejected) return null;

            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            // The manifest counts the same set as WorkspaceBackup: everything except SQLite's own sqlite_* tables.
            foreach (var table in tables.Where(t => !t.StartsWith("sqlite_", StringComparison.Ordinal)))
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
                counts[table] = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
            }

            return counts;
        }
        catch (SqliteException e)
        {
            reasons.Add($"The database schema could not be read (SQLite error {e.SqliteErrorCode}): {Safe(e.Message)}");
            return null;
        }
    }

    private static void VerifyManifest(
        string candidate, string? manifestPath, Dictionary<string, long>? counts, List<string> reasons, out string? note)
    {
        note = null;
        if (manifestPath is null || !File.Exists(manifestPath))
        {
            note = "No manifest came with the file, so its SHA-256 and row counts were not compared.";
            return;
        }

        BackupManifest manifest;
        try
        {
            manifest = BackupManifest.Read(manifestPath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            reasons.Add($"The manifest next to the file could not be read: {Safe(e.Message)}");
            return;
        }

        string actualHash;
        using (var stream = File.OpenRead(candidate))
            actualHash = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(actualHash, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
            reasons.Add("The file's SHA-256 does not match its manifest; the file changed after the backup was taken.");

        if (counts is null || manifest.TableCounts is null) return;
        foreach (var (table, expected) in manifest.TableCounts)
        {
            if (!counts.TryGetValue(table, out var actual))
                reasons.Add($"Table {Safe(table)} is in the manifest but not in the file.");
            else if (actual != expected)
                reasons.Add($"Table {Safe(table)} has {actual} rows but the manifest says {expected}.");
        }

        foreach (var table in counts.Keys.Where(t => !manifest.TableCounts.ContainsKey(t)))
            reasons.Add($"Table {Safe(table)} is in the file but not in the manifest.");
    }

    // Text read from an untrusted file ends up in messages: no control characters, bounded length.
    private static string Safe(string text)
    {
        var clean = new string(text.Select(c => char.IsControl(c) ? '?' : c).ToArray());
        return clean.Length <= 80 ? clean : clean[..80] + "...";
    }

    private static bool HasSqliteHeader(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buffer = new byte[SqliteHeader.Length];
            return stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false) == buffer.Length
                && buffer.AsSpan().SequenceEqual(SqliteHeader);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Copies <paramref name="pickedPath"/> into the staging folder, verifies the COPY and, only when it is
    /// accepted, writes the pending marker. A rejected file leaves nothing staged (an earlier pending
    /// restore is kept) and every reason comes back.
    /// </summary>
    public static async Task<RestoreVerification> StageAsync(string pickedPath, string appDataDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pickedPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        var picked = Path.GetFullPath(pickedPath);
        if (!File.Exists(picked))
            return new RestoreVerification(false, [$"The chosen file does not exist: {picked}"]);

        var final = StagingDirectory(appDataDirectory);
        var work = final + ".new";
        DeleteDirectory(work);
        CreatePrivateDirectory(work);

        try
        {
            var candidate = Path.Combine(work, CandidateName);
            string? stagedManifest = null;
            try
            {
                File.Copy(picked, candidate, overwrite: false);
                var sidecar = BackupManifest.PathFor(picked);
                if (File.Exists(sidecar))
                {
                    stagedManifest = BackupManifest.PathFor(candidate);
                    File.Copy(sidecar, stagedManifest, overwrite: false);
                }

                RestrictFile(candidate);
                if (stagedManifest is not null) RestrictFile(stagedManifest);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                DeleteDirectory(work);
                return new RestoreVerification(false, [$"The chosen file could not be read: {e.Message}"]);
            }

            var verification = await VerifyAsync(candidate, stagedManifest, ct).ConfigureAwait(false);
            if (!verification.Accepted)
            {
                DeleteDirectory(work);
                return verification;
            }

            string sha256;
            await using (var stream = File.OpenRead(candidate))
                sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));

            var pending = new PendingRestore(Path.GetFileName(picked), DateTimeOffset.UtcNow, sha256);
            var markerPath = Path.Combine(work, MarkerName);
            await File.WriteAllTextAsync(markerPath, JsonSerializer.Serialize(pending, Json), ct).ConfigureAwait(false);
            RestrictFile(markerPath);

            DeleteDirectory(final);   // replaces an earlier pending restore, now that the new one is verified
            Directory.Move(work, final);
            return verification;
        }
        catch
        {
            DeleteDirectory(work);
            throw;
        }
    }

    /// <summary>The restore waiting for the next launch, or null when there is none (or its marker cannot be read).</summary>
    public static PendingRestore? GetPending(string appDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        var staging = StagingDirectory(appDataDirectory);
        return File.Exists(Path.Combine(staging, CandidateName)) ? ReadMarker(Path.Combine(staging, MarkerName)) : null;
    }

    /// <summary>Removes the staging folder (candidate, its manifest and the marker) and nothing else.</summary>
    public static void CancelPending(string appDataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        DeleteDirectory(StagingDirectory(appDataDirectory));
    }

    /// <summary>
    /// Applies a pending restore. Call it before any workspace connection exists. Returns null when nothing
    /// is pending. A candidate that changed since it was verified is discarded and the live workspace is
    /// left alone. An <see cref="IOException"/> while moving the live file (on Windows an open MCP server
    /// holds it) leaves everything as it was and throws <see cref="WorkspaceUpgradeException"/>.
    /// </summary>
    public static async Task<RestoreOutcome?> ApplyPendingAsync(
        string workspacePath, string appDataDirectory, TimeProvider clock, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspacePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(appDataDirectory);
        ArgumentNullException.ThrowIfNull(clock);

        var staging = StagingDirectory(appDataDirectory);
        var markerPath = Path.Combine(staging, MarkerName);
        if (!File.Exists(markerPath)) return null;

        var pending = ReadMarker(markerPath);
        if (pending is null)
        {
            DeleteDirectory(staging);
            return new RestoreOutcome(false, "", null, "The pending restore could not be read and was discarded. The workspace was not changed.");
        }

        var candidate = Path.Combine(staging, CandidateName);
        string? problem = null;
        if (!File.Exists(candidate))
        {
            problem = "The staged backup is missing";
        }
        else
        {
            string actual;
            await using (var stream = File.OpenRead(candidate))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            if (!string.Equals(actual, pending.Sha256, StringComparison.OrdinalIgnoreCase))
                problem = "The staged backup changed after it was verified";
        }

        if (problem is not null)
        {
            DeleteDirectory(staging);
            return new RestoreOutcome(false, pending.SourceName, null, problem + ", so it was discarded. The workspace was not changed.");
        }

        var workspace = Path.GetFullPath(workspacePath);
        SqliteConnection.ClearAllPools();   // release any pooled handle that would hold the live files open

        var directory = Path.GetDirectoryName(workspace)!;
        var stamp = clock.GetUtcNow().UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);
        var stem = Path.Combine(directory, "workspace.pre-restore-" + stamp);
        for (var n = 1; AnyExists(stem); n++)
            stem = Path.Combine(directory, $"workspace.pre-restore-{stamp}-{n}");

        var moves = new List<(string From, string To)>();
        if (File.Exists(workspace)) moves.Add((workspace, stem + ".db"));
        if (File.Exists(workspace + "-wal")) moves.Add((workspace + "-wal", stem + ".db-wal"));
        if (File.Exists(workspace + "-shm")) moves.Add((workspace + "-shm", stem + ".db-shm"));

        var moved = new List<(string From, string To)>();
        try
        {
            foreach (var move in moves)
            {
                File.Move(move.From, move.To, overwrite: false);
                moved.Add(move);
            }

            File.Move(candidate, workspace, overwrite: false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            foreach (var (from, to) in moved)
            {
                try { File.Move(to, from, overwrite: false); } catch (Exception undo) when (undo is IOException or UnauthorizedAccessException) { }
            }

            throw new WorkspaceUpgradeException(
                $"The backup could not be restored because the current workspace is in use or cannot be moved ({e.Message}). " +
                "Close ads-mcp and any other program that is using the workspace, then start the app again; the restore is still pending. " +
                $"Nothing was deleted or replaced. File: {workspace}", e);
        }

        // The candidate is in place from here on, so the staging folder goes however the rest ends: a pending
        // marker that outlived its applied restore would be retried (and discarded) at every launch.
        try
        {
            // The live main file may have been absent (only a -wal moved): then there is no stem.db to restrict.
            if (!OperatingSystem.IsWindows() && File.Exists(stem + ".db"))
                File.SetUnixFileMode(stem + ".db", UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort: tightening the permissions of the kept copy must not fail an applied restore
        }
        finally
        {
            // A write-ahead log or shared-memory file left at the target could be replayed onto the restored file.
            DeleteFile(workspace + "-wal");
            DeleteFile(workspace + "-shm");
            DeleteDirectory(staging);
        }

        return new RestoreOutcome(true, pending.SourceName, moves.Count > 0 ? stem + ".db" : null, null);
    }

    private static PendingRestore? ReadMarker(string markerPath)
    {
        try
        {
            var pending = JsonSerializer.Deserialize<PendingRestore>(File.ReadAllText(markerPath), Json);
            return pending is { Sha256.Length: > 0 } ? pending : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static bool AnyExists(string stem) =>
        File.Exists(stem + ".db") || File.Exists(stem + ".db-wal") || File.Exists(stem + ".db-shm");

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(path);   // inherits the profile ACL of the app data folder
        else
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void RestrictFile(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    // Only ever called with the staging folder or its ".new" sibling.
    private static void DeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // Only ever called with the -wal/-shm sidecars at the restore target. A failure is not swallowed: a stale
    // log that stays could be replayed onto the restored file.
    private static void DeleteFile(string path) => File.Delete(path);
}
