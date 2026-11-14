using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AzureDesignStudio.Workspace;

/// <summary>Which pragmas <see cref="PragmaInterceptor"/> applies to a connection it sees opened.</summary>
public enum PragmaProfile
{
    /// <summary>The desktop writer: busy timeout, foreign keys and the WAL size limit.</summary>
    Writer,

    /// <summary>A read-only reader: busy timeout only (the connection string already forbids writes).</summary>
    Reader,

    /// <summary>A reader on a read-write connection: busy timeout plus <c>query_only</c>, so it still cannot write.</summary>
    QueryOnlyReader,
}

/// <summary>
/// Applies the per-connection pragmas on every open (SQLite keeps them per connection, and a pooled
/// connection can be handed to a context that never saw the first open). <c>synchronous</c> is left at
/// its durable default on purpose - in WAL mode NORMAL can roll back a committed transaction after
/// power loss. <c>journal_mode</c> is never set here: WAL is persistent in the file and is asserted
/// once by <see cref="WorkspaceSchema"/>.
/// </summary>
public sealed class PragmaInterceptor(PragmaProfile profile) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) =>
        Apply(connection);

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Apply(connection);
        return Task.CompletedTask;
    }

    private void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = profile switch
        {
            PragmaProfile.Writer =>
                $"PRAGMA busy_timeout={WorkspaceConnection.BusyTimeoutMilliseconds}; PRAGMA foreign_keys=ON; " +
                $"PRAGMA journal_size_limit={WorkspaceConnection.JournalSizeLimitBytes};",
            PragmaProfile.QueryOnlyReader =>
                $"PRAGMA busy_timeout={WorkspaceConnection.BusyTimeoutMilliseconds}; PRAGMA query_only=ON;",
            _ => $"PRAGMA busy_timeout={WorkspaceConnection.BusyTimeoutMilliseconds};",
        };
        command.ExecuteNonQuery();
    }
}

/// <summary>The rung of the reader ladder that opened the workspace.</summary>
public enum WorkspaceOpenMode
{
    /// <summary><c>Mode=ReadOnly</c>: the normal case, and the only one that sees a writer's live WAL.</summary>
    ReadOnly,

    /// <summary><c>Mode=ReadWrite</c> (never creating the file) with <c>query_only</c>: the WAL index could not be opened read-only.</summary>
    ReadWriteQueryOnly,

    /// <summary><c>immutable=1</c>: no locking at all, for a read-only directory with no live WAL. Can be stale if a writer starts later.</summary>
    Immutable,
}

/// <summary>The outcome of opening the workspace for reading: the rung, the connection string to use and a sentence saying why.</summary>
public sealed record WorkspaceReaderOpen(WorkspaceOpenMode Mode, string ConnectionString, string Detail);

/// <summary>What a trial open of one rung produced; <see cref="Code"/> is the primary SQLite result code.</summary>
internal readonly record struct ReaderProbe(bool Success, int Code, int ExtendedCode, string? Message)
{
    public static ReaderProbe Ok { get; } = new(true, 0, 0, null);

    public static ReaderProbe Failed(int code, int extendedCode, string message) => new(false, code, extendedCode, message);
}

/// <summary>
/// The one place that builds workspace connections: the desktop writer and the MCP reader. No shared
/// cache (Microsoft discourages it together with WAL); the pragmas that matter ride a connection
/// interceptor so they hold on every pooled open.
/// </summary>
public static class WorkspaceConnection
{
    public const int BusyTimeoutMilliseconds = 5000;
    public const long JournalSizeLimitBytes = 64L * 1024 * 1024;

    // SQLite result codes the ladder reacts to (primary codes).
    private const int SqliteReadOnly = 8;
    private const int SqliteCantOpen = 14;

    // Interceptors are stateless; sharing one instance per profile keeps EF's internal service-provider cache from growing.
    private static readonly PragmaInterceptor WriterInterceptor = new(PragmaProfile.Writer);
    private static readonly PragmaInterceptor ReaderInterceptor = new(PragmaProfile.Reader);
    private static readonly PragmaInterceptor QueryOnlyInterceptor = new(PragmaProfile.QueryOnlyReader);

    public static string WriterConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = true,
            ForeignKeys = true,
        }.ToString();

    public static DbContextOptions<WorkspaceDbContext> WriterOptions(string databasePath) =>
        new DbContextOptionsBuilder<WorkspaceDbContext>()
            .UseSqlite(WriterConnectionString(databasePath))
            .AddInterceptors(WriterInterceptor)
            .Options;

    public static DbContextOptions<WorkspaceDbContext> ReaderOptions(WorkspaceReaderOpen open)
    {
        ArgumentNullException.ThrowIfNull(open);
        return new DbContextOptionsBuilder<WorkspaceDbContext>()
            .UseSqlite(open.ConnectionString)
            .AddInterceptors(open.Mode == WorkspaceOpenMode.ReadWriteQueryOnly ? QueryOnlyInterceptor : ReaderInterceptor)
            .Options;
    }

    /// <summary>
    /// Opens an existing workspace for reading, descending only on a permission-shaped failure: read-only,
    /// then read-write with <c>query_only</c>, then <c>immutable=1</c> (only with no live WAL). A missing,
    /// corrupt or foreign file fails at once instead of descending. Never creates the file, never migrates.
    /// </summary>
    public static WorkspaceReaderOpen OpenReader(string databasePath) => OpenReader(databasePath, RealProbe);

    internal static WorkspaceReaderOpen OpenReader(string databasePath, Func<string, ReaderProbe> probe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentNullException.ThrowIfNull(probe);
        var full = Path.GetFullPath(databasePath);
        if (!File.Exists(full))
            throw new InvalidOperationException($"The workspace database does not exist: {full}");

        var readOnly = ReadOnlyConnectionString(full);
        var first = probe(readOnly);
        if (first.Success)
            return new WorkspaceReaderOpen(WorkspaceOpenMode.ReadOnly, readOnly, "opened read-only");
        EnsurePermissionShaped(full, first, "read-only");

        var readWrite = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            Mode = SqliteOpenMode.ReadWrite,   // ReadWrite never creates the file
            Pooling = true,
        }.ToString();
        var second = probe(readWrite);
        if (second.Success)
            return new WorkspaceReaderOpen(WorkspaceOpenMode.ReadWriteQueryOnly, readWrite,
                $"read-only open failed ({Describe(first)}); opened read-write with query_only, so it still cannot write");
        EnsurePermissionShaped(full, second, "read-write with query_only");

        if (HasLiveWal(full))
            throw new InvalidOperationException(
                $"The workspace database {full} has a write-ahead log in use and its directory does not allow the reader to open the log index " +
                $"(read-only: {Describe(first)}; read-write: {Describe(second)}). Fix the directory permissions (the reader needs write access to the folder), " +
                "or close the app so the log is checkpointed.");

        var immutable = new SqliteConnectionStringBuilder
        {
            DataSource = new Uri(full).AbsoluteUri + "?immutable=1",
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = true,
        }.ToString();
        var third = probe(immutable);
        if (third.Success)
            return new WorkspaceReaderOpen(WorkspaceOpenMode.Immutable, immutable,
                $"read-only and read-write opens failed ({Describe(first)}; {Describe(second)}); opened immutable=1 without locking - " +
                "the content can be stale or inconsistent if a writer starts later");

        throw new InvalidOperationException(
            $"The workspace database {full} could not be opened in any mode ({Describe(first)}; {Describe(second)}; {Describe(third)}). " +
            "The folder is read-only and the reader cannot create the shared-memory index it needs: fix the directory permissions.");
    }

    private static string ReadOnlyConnectionString(string full) =>
        new SqliteConnectionStringBuilder { DataSource = full, Mode = SqliteOpenMode.ReadOnly, Pooling = true }.ToString();

    private static void EnsurePermissionShaped(string path, ReaderProbe probe, string rung)
    {
        if (probe.Code is SqliteReadOnly or SqliteCantOpen) return;
        throw new InvalidOperationException(
            $"The workspace database {path} could not be read ({rung}, SQLite error {probe.Code}): {probe.Message}. " +
            "This is not a permission problem, so no fallback was tried.");
    }

    private static string Describe(ReaderProbe probe) =>
        $"SQLite error {probe.Code}" + (probe.ExtendedCode != 0 && probe.ExtendedCode != probe.Code ? $"/{probe.ExtendedCode}" : "") +
        (string.IsNullOrEmpty(probe.Message) ? "" : $": {probe.Message}");

    private static bool HasLiveWal(string full)
    {
        var wal = new FileInfo(full + "-wal");
        return wal.Exists && wal.Length > 0;
    }

    // An unpooled trial open: runs the reader pragmas and touches the schema, so a WAL that needs recovery or an index
    // that cannot be opened fails here and not in the first query.
    private static ReaderProbe RealProbe(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString) { Pooling = false };
        try
        {
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();
            using var pragma = connection.CreateCommand();
            pragma.CommandText = $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};";
            pragma.ExecuteNonQuery();
            using var read = connection.CreateCommand();
            read.CommandText = "SELECT 1 FROM sqlite_master LIMIT 1";
            read.ExecuteScalar();
            return ReaderProbe.Ok;
        }
        catch (SqliteException e)
        {
            return ReaderProbe.Failed(e.SqliteErrorCode, e.SqliteExtendedErrorCode, e.Message);
        }
    }
}
