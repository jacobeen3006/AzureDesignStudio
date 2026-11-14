using AzureDesignStudio.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace AzureDesignStudio.Server.Persistence;

/// <summary>What the bootstrapper does with a SQL Server database at startup.</summary>
internal enum BootstrapAction
{
    /// <summary>Apply the pending migrations (a no-op when there are none).</summary>
    Migrate,

    /// <summary>The database does not exist and the operator did not allow creating it.</summary>
    RefuseMissingDatabase,

    /// <summary>Migrations are pending and the operator switched migrating off.</summary>
    RefusePendingMigrations,
}

/// <summary>
/// Brings the server database schema up once at startup. SQL Server is migrated with the EF
/// migrations in this assembly. <c>Sqlite</c> and <c>InMemory</c>, the documented development and
/// test providers, have no migrations and keep a model-creation call: the only one in Server and
/// Services, and nothing calls it on a request path.
/// </summary>
internal static class DatabaseBootstrapper
{
    public const string MigrateKey = "Database:Migrate";
    public const string AllowCreateKey = "Database:AllowCreate";

    public static void Initialize(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<DesignDbContext>();
        var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>()
            .CreateLogger("AzureDesignStudio.Server.Persistence.DatabaseBootstrapper");

        if (db.Database.IsInMemory())
            logger.LogWarning("Database:Provider is InMemory: designs, links and approvals are lost when the server stops. Use it for testing only.");
        else if (db.Database.IsSqlite())
            logger.LogWarning("Database:Provider is Sqlite: the server database is a single local file. Use it for testing and development only.");

        if (db.Database.IsSqlServer())
        {
            MigrateSqlServer(db, scope.ServiceProvider.GetRequiredService<IConfiguration>(), logger);
            return;
        }

        db.Database.EnsureCreated();
    }

    /// <summary>
    /// The startup decision as a pure function. A missing database without <paramref name="allowCreate"/>
    /// is refused first (a typo in the connection string must not yield a silent empty database); then
    /// pending migrations with <paramref name="migrateEnabled"/> off are refused, a missing database
    /// counting as having every migration pending. Anything else migrates.
    /// </summary>
    internal static BootstrapAction Decide(bool migrateEnabled, bool allowCreate, bool databaseExists, int pendingMigrationCount)
    {
        if (!databaseExists && !allowCreate)
            return BootstrapAction.RefuseMissingDatabase;
        if (!migrateEnabled && pendingMigrationCount > 0)
            return BootstrapAction.RefusePendingMigrations;
        return BootstrapAction.Migrate;
    }

    private static void MigrateSqlServer(DesignDbContext db, IConfiguration configuration, ILogger logger)
    {
        var migrateEnabled = ReadFlag(configuration, MigrateKey, defaultValue: true);
        var allowCreate = ReadFlag(configuration, AllowCreateKey, defaultValue: false);

        var exists = db.GetService<IRelationalDatabaseCreator>().Exists();
        var pending = exists
            ? db.Database.GetPendingMigrations().Count()
            : db.Database.GetMigrations().Count();

        switch (Decide(migrateEnabled, allowCreate, exists, pending))
        {
            case BootstrapAction.RefuseMissingDatabase:
                throw new InvalidOperationException(
                    "The SQL Server database in the connection string does not exist, and the server will not create it. " +
                    $"{AllowCreateKey}=true lets the server create it; it is meant for the first deployment only, " +
                    "so check the connection string for a typo before setting it. See docs/database-operations.md.");

            case BootstrapAction.RefusePendingMigrations:
                throw new InvalidOperationException(
                    $"The SQL Server database has {pending} pending migration(s) and {MigrateKey} is false, so the server will not start. " +
                    "Apply them with: dotnet ef database update --context SqlServerDesignDbContext --project src/AzureDesignStudio.Server " +
                    $"(or set {MigrateKey}=true after taking a backup).");

            default:
                var applied = db.Database.GetPendingMigrations().ToList();
                db.Database.Migrate();
                if (applied.Count > 0)
                    logger.LogInformation("Applied {Count} database migration(s): {Migrations}", applied.Count, string.Join(", ", applied));
                break;
        }
    }

    /// <summary>A boolean setting; a value that is not true or false stops the host instead of guessing.</summary>
    private static bool ReadFlag(IConfiguration configuration, string key, bool defaultValue)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;
        if (bool.TryParse(value.Trim(), out var flag))
            return flag;
        throw new InvalidOperationException($"{key} is \"{value}\", which is not true or false.");
    }
}
