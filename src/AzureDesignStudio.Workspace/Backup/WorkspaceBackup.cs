using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace AzureDesignStudio.Workspace;

/// <summary>A finished backup: the database file, its manifest sidecar and the manifest content.</summary>
public sealed record BackupResult(string Path, string ManifestPath, BackupManifest Manifest);

/// <summary>
/// WAL-safe backups of a SQLite database through <c>VACUUM INTO</c>. The result is a consistent snapshot
/// that includes commits still in the live <c>-wal</c> file and is a rollback-journal file; copying the
/// database file would lose those commits, so it is never done. Generic over the file stem so the
/// <c>deploy.db</c> backups can reuse it.
/// </summary>
public static class WorkspaceBackup
{
    /// <summary>A backup needs this multiple of the source size free on the target volume.</summary>
    private const double FreeSpaceFactor = 1.2;

    public static Task<BackupResult> CreateAsync(
        string sourceDatabasePath, string backupsDirectory, BackupReason reason, DateTimeOffset nowUtc,
        string stem = "workspace", CancellationToken ct = default) =>
        CreateAsync(sourceDatabasePath, backupsDirectory, reason, nowUtc, stem, freeSpaceProbe: null, ct);

    // The probe is a parameter (not a static switch) so a test lowering it cannot affect tests running in parallel.
    internal static async Task<BackupResult> CreateAsync(
        string sourceDatabasePath, string backupsDirectory, BackupReason reason, DateTimeOffset nowUtc,
        string stem, Func<string, long>? freeSpaceProbe, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDatabasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupsDirectory);
        var source = Path.GetFullPath(sourceDatabasePath);
        if (!File.Exists(source))
            throw new FileNotFoundException($"The database to back up does not exist: {source}", source);

        var directory = Path.GetFullPath(backupsDirectory);
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        var finalPath = Path.Combine(directory, BackupNaming.FileName(stem, nowUtc, reason));
        var manifestPath = BackupManifest.PathFor(finalPath);
        if (File.Exists(finalPath))
            throw new IOException($"A backup named {Path.GetFileName(finalPath)} already exists; it is never overwritten.");

        var sourceSize = new FileInfo(source).Length;
        var needed = (long)Math.Ceiling(sourceSize * FreeSpaceFactor);
        var free = (freeSpaceProbe ?? DefaultFreeSpace)(directory);
        if (free >= 0 && free < needed)
            throw new IOException(
                $"Not enough free space for a backup in {directory}: {needed:N0} bytes needed " +
                $"({FreeSpaceFactor:0.0} x the {sourceSize:N0}-byte database), {free:N0} free, short by {needed - free:N0}.");

        var tempPath = finalPath + ".tmp";
        var tempManifestPath = manifestPath + ".tmp";
        DeleteIfExists(tempPath);
        DeleteIfExists(tempManifestPath);

        try
        {
            await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = source,
                Mode = SqliteOpenMode.ReadWrite,   // never creates the source
                Pooling = false,
            }.ToString()))
            {
                await connection.OpenAsync(ct).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "VACUUM INTO $path";   // a bound parameter, never a concatenated path
                command.Parameters.AddWithValue("$path", tempPath);
                await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            var check = WorkspaceIntegrity.Check(tempPath, IntegrityDepth.Quick, IntegrityAccess.Snapshot);
            if (check.Outcome != IntegrityOutcome.Ok)
                throw new IOException(
                    $"The backup of {source} failed its integrity check ({check.Outcome}: {string.Join("; ", check.Problems)}) and was discarded.");

            var (counts, migrationId) = Inspect(tempPath);
            string sha256;
            await using (var stream = File.OpenRead(tempPath))
                sha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            var manifest = new BackupManifest(
                nowUtc.ToUniversalTime(), reason, AppVersion, migrationId, counts, sha256,
                new FileInfo(tempPath).Length, Path.GetFileName(source));

            manifest.Write(tempManifestPath);
            File.Move(tempManifestPath, manifestPath, overwrite: true);
            File.Move(tempPath, finalPath, overwrite: false);   // an existing target is refused

            if (!OperatingSystem.IsWindows())
            {
                const UnixFileMode userOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                File.SetUnixFileMode(finalPath, userOnly);
                File.SetUnixFileMode(manifestPath, userOnly);
            }

            return new BackupResult(finalPath, manifestPath, manifest);
        }
        catch
        {
            DeleteIfExists(tempPath);
            DeleteIfExists(tempManifestPath);
            throw;
        }
    }

    /// <summary>
    /// The backups in a directory whose NAME parses (everything else is ignored), newest first.
    /// A missing directory is an empty list.
    /// </summary>
    public static IReadOnlyList<BackupFile> List(string backupsDirectory, string stem = "workspace")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupsDirectory);
        var directory = Path.GetFullPath(backupsDirectory);
        if (!Directory.Exists(directory)) return [];

        var files = new List<BackupFile>();
        foreach (var path in Directory.EnumerateFiles(directory))
        {
            if (!BackupNaming.TryParse(Path.GetFileName(path), stem, out var created, out var reason)) continue;
            try
            {
                files.Add(new BackupFile(path, created, reason, new FileInfo(path).Length));
            }
            catch (IOException)
            {
                // removed between the listing and the stat
            }
        }

        return files
            .OrderByDescending(f => f.CreatedUtc)
            .ThenBy(f => Path.GetFileName(f.Path), StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Applies <see cref="BackupRetention.Plan"/> to <see cref="List"/> and deletes the planned backups
    /// (the <c>.db</c> and its manifest). Only files whose full path is inside the backups directory are
    /// touched; a file that cannot be deleted (in use, no permission) is reported through
    /// <paramref name="onProblem"/> and skipped. Returns the backups that were deleted.
    /// </summary>
    public static Task<IReadOnlyList<BackupFile>> PruneAsync(
        string backupsDirectory, DateTimeOffset now, string stem = "workspace", RetentionPolicy? policy = null,
        CancellationToken ct = default, Action<string>? onProblem = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupsDirectory);
        var directory = Path.GetFullPath(backupsDirectory);
        var prefix = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        var plan = BackupRetention.Plan(List(directory, stem), now, policy, HoldsData);
        var deleted = new List<BackupFile>();
        foreach (var file in plan.Delete)
        {
            ct.ThrowIfCancellationRequested();
            var full = Path.GetFullPath(file.Path);
            if (!full.StartsWith(prefix, comparison))
            {
                onProblem?.Invoke($"Skipped {full}: it is outside the backups directory {directory}.");
                continue;
            }

            try
            {
                File.Delete(full);
                File.Delete(BackupManifest.PathFor(full));
                deleted.Add(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                onProblem?.Invoke($"Could not delete {full}: {e.Message}");
            }
        }

        return Task.FromResult<IReadOnlyList<BackupFile>>(deleted);
    }

    /// <summary>
    /// Whether the manifest says the backup holds user data: any table other than the EF bookkeeping tables
    /// has rows. A missing or unreadable manifest counts as "holds data": retention keeps at most two such
    /// backups, which is cheaper than deleting the only copy of something it could not read.
    /// </summary>
    internal static bool HoldsData(BackupFile file)
    {
        try
        {
            var manifest = BackupManifest.Read(BackupManifest.PathFor(file.Path));
            return manifest.TableCounts is null
                || manifest.TableCounts.Any(t => t.Value > 0 && !t.Key.StartsWith("__EF", StringComparison.Ordinal));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
        {
            return true;
        }
    }

    private static string AppVersion { get; } =
        typeof(WorkspaceBackup).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(WorkspaceBackup).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private static long DefaultFreeSpace(string directory)
    {
        try
        {
            var root = Path.GetPathRoot(directory);
            return string.IsNullOrEmpty(root) ? -1 : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return -1;   // unknown: do not refuse a backup on a volume we cannot measure
        }
    }

    private static (SortedDictionary<string, long> Counts, string? LatestMigrationId) Inspect(string path)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();

        var tables = new List<string>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite\\_%' ESCAPE '\\' ORDER BY name";
            using var reader = command.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }

        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var table in tables)
        {
            using var command = connection.CreateCommand();
            command.CommandText = $"SELECT COUNT(*) FROM \"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
            counts[table] = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        }

        string? latest = null;
        if (tables.Contains("__EFMigrationsHistory", StringComparer.Ordinal))
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT MAX(\"MigrationId\") FROM \"__EFMigrationsHistory\"";
            latest = command.ExecuteScalar() as string;
        }

        return (counts, latest);
    }

    private static void DeleteIfExists(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }
}
