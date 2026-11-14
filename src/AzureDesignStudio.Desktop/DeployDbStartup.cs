using AzureDesignStudio.Services.Models;
using AzureDesignStudio.Workspace;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureDesignStudio.Desktop;

/// <summary>
/// Brings the desktop <c>deploy.db</c> (approvals, guardrails, linked subscriptions, audit events)
/// to the current schema through EF migrations. A plain C# file with no platform conditionals, so
/// the Server tests compile it and exercise it on real SQLite files.
/// </summary>
/// <remarks>
/// <para>
/// Order: (1) when an existing file has pending migrations, a <c>premigration</c> backup goes to
/// <c>backups/deploy</c> and a failure aborts the upgrade with the file untouched; (2) a file that
/// has no migration history yet gains the four columns that later releases added (SQLite has no
/// conditional ADD COLUMN, so the guarded baseline cannot do it); (3) <c>Migrate</c>; (4) prune.
/// </para>
/// <para>
/// The backups are private on purpose. <c>AzureSubscriptions.ClientSecret</c> rides into every copy
/// of this file, so the copies live only in their own folder under the stem <c>deploy</c>: the
/// workspace backup list (stem <c>workspace</c>, the folder above) never returns them, they are not
/// offered for restore or for a support bundle, and on Unix the folders and files are user-only.
/// </para>
/// </remarks>
internal static class DeployDbStartup
{
    /// <summary>The file stem of the deploy backups and the name of their folder under <c>backups</c>.</summary>
    internal const string BackupStem = "deploy";

    /// <summary>How many deploy backups are kept; none is kept by age alone.</summary>
    internal const int BackupsKept = 3;

    // Columns later releases added to tables an older deploy.db already has. Constants only: they
    // are the only identifiers that ever reach the ALTER TABLE text.
    private static readonly (string Table, string Column)[] LegacyColumns =
    [
        ("AdsPlanApprovals", "RevertPlanJson"),
        ("AdsPlanApplyEvents", "RevertPlanHash"),
        ("AdsPlanApplyEvents", "RevertPlanJson"),
        ("AdsPlanApplyEvents", "TargetScope"),
    ];

    private const UnixFileMode UserOnlyDirectory =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    /// <summary>Upgrades <paramref name="db"/>'s file; throws <see cref="InvalidOperationException"/> when the backup cannot be written.</summary>
    public static void Migrate(DesignDbContext db, string deployDbPath, string backupsDirectory, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentException.ThrowIfNullOrWhiteSpace(deployDbPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupsDirectory);
        var now = (clock ?? TimeProvider.System).GetUtcNow();

        var existed = File.Exists(deployDbPath) && new FileInfo(deployDbPath).Length > 0;
        var pending = db.Database.GetPendingMigrations().Any();

        if (existed && pending)
            Backup(deployDbPath, backupsDirectory, now);

        if (existed)
            AddMissingLegacyColumns(deployDbPath);

        db.Database.Migrate();

        Prune(backupsDirectory, now);
    }

    private static void Backup(string deployDbPath, string backupsDirectory, DateTimeOffset now)
    {
        try
        {
            CreateUserOnlyDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(backupsDirectory)));
            CreateUserOnlyDirectory(backupsDirectory);

            // The backup is async all the way down; a hop to the pool keeps a UI-thread caller from deadlocking.
            Task.Run(() => WorkspaceBackup.CreateAsync(
                    deployDbPath, backupsDirectory, BackupReason.Premigration, now, BackupStem))
                .GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException
                                      or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException(
                $"The deploy database was not upgraded: the backup taken before the upgrade could not be written " +
                $"to {backupsDirectory}, so {deployDbPath} is unchanged. {e.Message}", e);
        }
    }

    private static void CreateUserOnlyDirectory(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, UserOnlyDirectory);
        File.SetUnixFileMode(path, UserOnlyDirectory);
    }

    /// <summary>
    /// A file built before migrations has no history table and may predate four columns. Add them
    /// (nullable TEXT, appended) where the table exists and the column does not. Unpooled and
    /// closed before EF runs.
    /// </summary>
    private static void AddMissingLegacyColumns(string deployDbPath)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = deployDbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();

        if (Count(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = '__EFMigrationsHistory';") != 0)
            return;   // already under migrations

        foreach (var (table, column) in LegacyColumns)
        {
            if (Count(connection, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $table;", table) == 0)
                continue;   // the baseline creates it, with the column
            if (Count(connection, "SELECT COUNT(*) FROM pragma_table_info($table) WHERE name = $column;", table, column) != 0)
                continue;

            using var add = connection.CreateCommand();
            add.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" TEXT;";
            add.ExecuteNonQuery();
        }
    }

    private static long Count(SqliteConnection connection, string sql, string? table = null, string? column = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (table != null) command.Parameters.AddWithValue("$table", table);
        if (column != null) command.Parameters.AddWithValue("$column", column);
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Prune(string backupsDirectory, DateTimeOffset now)
    {
        try
        {
            WorkspaceBackup.PruneAsync(backupsDirectory, now, BackupStem, new RetentionPolicy(BackupsKept, TimeSpan.Zero))
                .GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Old backups outstaying their welcome must never stop the app from starting.
        }
    }
}
