using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;

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
    /// Verifies a candidate file in place: it exists and is not empty, starts with the SQLite header and
    /// passes <c>integrity_check</c>. Callers verify the staged copy, never the user's original.
    /// </summary>
    public static Task<RestoreVerification> VerifyAsync(string candidatePath, string? manifestPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(candidatePath);
        return Task.Run(() => Verify(Path.GetFullPath(candidatePath)), ct);
    }

    private static RestoreVerification Verify(string candidate)
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
            reasons.Add($"The database failed its integrity check ({integrity.Outcome}): {string.Join("; ", integrity.Problems)}");

        return new RestoreVerification(reasons.Count == 0, reasons);
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

        if (!OperatingSystem.IsWindows() && moves.Count > 0)
            File.SetUnixFileMode(stem + ".db", UnixFileMode.UserRead | UnixFileMode.UserWrite);

        // A write-ahead log or shared-memory file left at the target could be replayed onto the restored file.
        DeleteFile(workspace + "-wal");
        DeleteFile(workspace + "-shm");
        DeleteDirectory(staging);

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
