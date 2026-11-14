using AzureDesignStudio.Services;
using Microsoft.EntityFrameworkCore;

namespace AzureDesignStudio.Server.Persistence;

/// <summary>The database engines the server can run on.</summary>
internal enum DatabaseKind
{
    SqlServer,
    Sqlite,
    InMemory,
}

/// <summary>
/// Picks the Entity Framework provider from configuration alone, so Debug and Release builds
/// select identically. <c>Database:Provider</c> is <c>SqlServer</c> (the default), <c>Sqlite</c>
/// or <c>InMemory</c>.
/// </summary>
internal static class DatabaseProvider
{
    public const string Key = "Database:Provider";
    public const string InMemoryNameKey = "Database:InMemoryName";
    public const string DefaultInMemoryName = "adsdb-inmem";

    private const string NoDatabaseMessage =
        "No database is configured. Name a Key Vault that holds an \"ads-main\" connection string (KeyVaultName), " +
        "or set ConnectionStrings__ads-main. Database__Provider=InMemory starts the server without one, for testing only.";

    /// <summary>The configured provider. A missing value is SQL Server, so a forgotten setting
    /// fails fast for want of a connection string instead of silently losing data.</summary>
    public static DatabaseKind Resolve(IConfiguration configuration)
    {
        var value = configuration[Key];
        if (string.IsNullOrWhiteSpace(value))
            return DatabaseKind.SqlServer;

        var trimmed = value.Trim();
        if (string.Equals(trimmed, nameof(DatabaseKind.SqlServer), StringComparison.OrdinalIgnoreCase))
            return DatabaseKind.SqlServer;
        if (string.Equals(trimmed, nameof(DatabaseKind.Sqlite), StringComparison.OrdinalIgnoreCase))
            return DatabaseKind.Sqlite;
        if (string.Equals(trimmed, nameof(DatabaseKind.InMemory), StringComparison.OrdinalIgnoreCase))
            return DatabaseKind.InMemory;

        throw new InvalidOperationException(
            $"{Key} is \"{value}\", which is not a database provider. Accepted values: " +
            $"{nameof(DatabaseKind.SqlServer)} (the default), {nameof(DatabaseKind.Sqlite)}, {nameof(DatabaseKind.InMemory)}.");
    }

    /// <summary>Configures the context options for the resolved provider. Runs when the first
    /// context is built, so a connection string from the vault source is already loaded.</summary>
    public static void Configure(DbContextOptionsBuilder options, IConfiguration configuration) =>
        Configure(options, Resolve(configuration), configuration);

    /// <summary>Configures the context options for an already resolved provider. The host resolves
    /// <c>Database:Provider</c> once, before the vault decision, and passes that same value here, so the
    /// vault and the EF provider can never be chosen from two different reads.</summary>
    public static void Configure(DbContextOptionsBuilder options, DatabaseKind kind, IConfiguration configuration)
    {
        // "ads-main" is both the vault secret name and the connection-string key.
        var connectionString = configuration["ads-main"] ?? configuration.GetConnectionString("ads-main");

        if (kind == DatabaseKind.InMemory)
        {
            var name = configuration[InMemoryNameKey];
            options.UseInMemoryDatabase(string.IsNullOrWhiteSpace(name) ? DefaultInMemoryName : name);
        }
        else if (kind == DatabaseKind.Sqlite && ServiceTools.IsConfigured(connectionString))
        {
            options.UseSqlite(connectionString);
        }
        else if (kind == DatabaseKind.SqlServer && ServiceTools.IsConfigured(connectionString))
        {
            options.UseSqlServer(connectionString);
        }
        else
        {
            throw new InvalidOperationException(NoDatabaseMessage);
        }
    }
}
