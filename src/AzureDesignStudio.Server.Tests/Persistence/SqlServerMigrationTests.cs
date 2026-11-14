using AzureDesignStudio.Server.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AzureDesignStudio.Server.Tests.Persistence;

/// <summary>
/// The server migrations against a real SQL Server 2022 in a throwaway container. Skipped, never
/// failed, when <c>ADS_TEST_SQLSERVER</c> is unset (see src/RunSqlServerTests.sh and the
/// <c>sqlserver</c> CI job). Every test uses its own uniquely named database.
/// </summary>
[Trait("Category", "SqlServer")]
public sealed class SqlServerMigrationTests
{
    private static readonly string[] Tables =
    [
        "AdsDesigns",
        "AzureSubscriptions",
        "AdsPlanApprovals",
        "AdsPlanApplyEvents",
        "AdsGuardrailPolicies",
        "AdsDecomApprovals",
        "AdsDecomWindowEvents",
    ];

    private static Dictionary<string, int> RowCounts(string connectionString) =>
        Tables.ToDictionary(t => t, t => SqlServerDatabaseFixture.Scalar<int>(connectionString, $"SELECT COUNT(*) FROM [{t}]"));

    [SqlServerFact]
    public void LegacyUpgrade_KeepsRowsAndWritesHistory()
    {
        using var fixture = new SqlServerDatabaseFixture();
        fixture.ExecuteScript(SqlServerDatabaseFixture.LegacyScript());
        var before = RowCounts(fixture.ConnectionString);
        Assert.All(before.Values, count => Assert.True(count >= 1));

        using (var db = fixture.CreateContext())
            db.Database.Migrate();

        Assert.Equal(before, RowCounts(fixture.ConnectionString));
        Assert.Equal(1, SqlServerDatabaseFixture.Scalar<int>(fixture.ConnectionString,
            "SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId LIKE N'%\\_Baseline' ESCAPE N'\\'"));

        foreach (var (table, column) in new[]
                 {
                     ("AdsPlanApprovals", "RevertPlanJson"),
                     ("AdsPlanApplyEvents", "TargetScope"),
                     ("AdsPlanApplyEvents", "RevertPlanHash"),
                     ("AdsPlanApplyEvents", "RevertPlanJson"),
                 })
        {
            Assert.NotNull(SqlServerDatabaseFixture.Scalar<int?>(fixture.ConnectionString,
                $"SELECT COL_LENGTH(N'{table}', N'{column}')"));
        }

        // TenantGuardrails: the new tables exist and are empty, the tenant column is nullable, and
        // the seeded legacy guardrail row is untouched (NULL tenant = the inherited default).
        foreach (var table in new[] { "AdsTenantSettings", "AdsSecurityEvents" })
        {
            Assert.NotNull(SqlServerDatabaseFixture.Scalar<int?>(fixture.ConnectionString, $"SELECT OBJECT_ID(N'{table}', N'U')"));
            Assert.Equal(0, SqlServerDatabaseFixture.Scalar<int>(fixture.ConnectionString, $"SELECT COUNT(*) FROM [{table}]"));
        }

        Assert.Equal(1, SqlServerDatabaseFixture.Scalar<int>(fixture.ConnectionString, """
            SELECT COUNT(*) FROM sys.columns
            WHERE object_id = OBJECT_ID(N'AdsGuardrailPolicies') AND name = N'TenantId' AND is_nullable = 1
            """));
        Assert.Equal(before["AdsGuardrailPolicies"], SqlServerDatabaseFixture.Scalar<int>(fixture.ConnectionString,
            "SELECT COUNT(*) FROM AdsGuardrailPolicies WHERE TenantId IS NULL"));
        Assert.Equal(1, SqlServerDatabaseFixture.Scalar<int>(fixture.ConnectionString,
            "SELECT COUNT(*) FROM AdsGuardrailPolicies WHERE Id = 1 AND TenantId IS NULL"));
        Assert.Equal(1, SqlServerDatabaseFixture.Scalar<int>(fixture.ConnectionString, """
            SELECT COUNT(*) FROM sys.indexes
            WHERE object_id = OBJECT_ID(N'AdsGuardrailPolicies') AND name = N'IX_AdsGuardrailPolicies_TenantId'
              AND is_unique = 1 AND has_filter = 1
            """));
        // No default constraint was left behind on the new column or the new tables.
        Assert.Equal(0, SqlServerDatabaseFixture.Scalar<int>(fixture.ConnectionString, """
            SELECT COUNT(*) FROM sys.default_constraints
            WHERE parent_object_id IN (OBJECT_ID(N'AdsTenantSettings'), OBJECT_ID(N'AdsSecurityEvents'))
               OR (parent_object_id = OBJECT_ID(N'AdsGuardrailPolicies'))
            """));
    }

    [SqlServerFact]
    public void TheFilteredUniqueIndex_AllowsManyDefaultRowsAndRefusesATenantTwice()
    {
        using var fixture = new SqlServerDatabaseFixture();
        Migrate(fixture.ConnectionString);
        const string insert = "INSERT INTO AdsGuardrailPolicies (Id, TenantId, Json, UpdatedBy, UpdatedByUserId, UpdatedAtUtc) VALUES ";
        SqlServerDatabaseFixture.ExecuteScript(fixture.ConnectionString,
            insert + "(1, NULL, N'{}', N'a', N'a', SYSUTCDATETIME()), (2, NULL, N'{}', N'a', N'a', SYSUTCDATETIME()), "
                   + "(3, '55555555-5555-5555-5555-555555555555', N'{}', N'a', N'a', SYSUTCDATETIME())");

        Assert.ThrowsAny<SqlException>(() => SqlServerDatabaseFixture.ExecuteScript(fixture.ConnectionString,
            insert + "(4, '55555555-5555-5555-5555-555555555555', N'{}', N'a', N'a', SYSUTCDATETIME())"));
    }

    private static void Migrate(string connectionString)
    {
        using var db = SqlServerDatabaseFixture.CreateContext(connectionString);
        db.Database.Migrate();
    }

    private static string ApprovedAtUtcDefault(string connectionString) =>
        SqlServerDatabaseFixture.Scalar<string>(connectionString, """
            SELECT d.name FROM sys.default_constraints d
            JOIN sys.columns c ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id
            WHERE d.parent_object_id = OBJECT_ID(N'AdsPlanApprovals') AND c.name = N'ApprovedAtUtc'
            """);

    [SqlServerFact]
    public void LegacyUpgrade_MatchesAFreshBaselineDatabase()
    {
        using var legacy = new SqlServerDatabaseFixture();
        using var fresh = new SqlServerDatabaseFixture();
        legacy.ExecuteScript(SqlServerDatabaseFixture.LegacyScript());

        // The fixture really carries the legacy-only default; without it the comparison proves nothing.
        Assert.NotNull(ApprovedAtUtcDefault(legacy.ConnectionString));
        Assert.Contains(SqlServerSchemaDescriber.Defaults(legacy.ConnectionString),
            d => d.StartsWith("default AdsPlanApprovals.ApprovedAtUtc ", StringComparison.Ordinal));

        Migrate(legacy.ConnectionString);
        Migrate(fresh.ConnectionString);

        Assert.Null(ApprovedAtUtcDefault(legacy.ConnectionString));
        Assert.Null(ApprovedAtUtcDefault(fresh.ConnectionString));
        Assert.Equal(
            SqlServerSchemaDescriber.Describe(fresh.ConnectionString),
            SqlServerSchemaDescriber.Describe(legacy.ConnectionString));
    }

    [SqlServerFact]
    public void FreshBaseline_MatchesAModelCreatedDatabase()
    {
        using var migrated = new SqlServerDatabaseFixture();
        using var created = new SqlServerDatabaseFixture();

        Migrate(migrated.ConnectionString);
        using (var db = created.CreateContext())
        {
            // Test-only: the model-created schema is the reference the baseline text must agree with.
            Assert.True(db.Database.EnsureCreated());
        }

        Assert.Equal(
            SqlServerSchemaDescriber.Describe(created.ConnectionString),
            SqlServerSchemaDescriber.Describe(migrated.ConnectionString));
    }

    [SqlServerFact]
    public void LegacyUpgrade_HasNoPendingModelChanges()
    {
        using var fixture = new SqlServerDatabaseFixture();
        fixture.ExecuteScript(SqlServerDatabaseFixture.LegacyScript());

        using var db = fixture.CreateContext();
        db.Database.Migrate();

        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Empty(db.Database.GetPendingMigrations());
    }

    [SqlServerFact]
    public async Task ConcurrentMigrate_ThreeCallers_AllSucceed()
    {
        using var fixture = new SqlServerDatabaseFixture();

        // Three replicas (or processes) starting together; EF serialises them on a database-wide lock.
        var callers = Enumerable.Range(0, 3).Select(_ => Task.Run(() => Migrate(fixture.ConnectionString)));
        await Task.WhenAll(callers);

        using var db = fixture.CreateContext();
        var migrations = db.Database.GetMigrations().ToList();
        Assert.Equal(migrations.Count, SqlServerDatabaseFixture.Scalar<int>(fixture.ConnectionString,
            "SELECT COUNT(*) FROM __EFMigrationsHistory"));
        Assert.Equal(migrations.Count, SqlServerDatabaseFixture.Scalar<int>(fixture.ConnectionString,
            "SELECT COUNT(DISTINCT MigrationId) FROM __EFMigrationsHistory"));
    }

    [SqlServerFact]
    public void BackupMigrateRestore_ReturnsToThePreUpgradeState()
    {
        using var fixture = new SqlServerDatabaseFixture();
        fixture.ExecuteScript(SqlServerDatabaseFixture.LegacyScript());
        var db = fixture.DatabaseName;
        // The path is inside the SQL Server container, which is why these tests need a container.
        var backup = $"/var/opt/mssql/data/{db}-pre.bak";

        SqlServerDatabaseFixture.ExecuteScript(fixture.MasterConnectionString,
            $"BACKUP DATABASE [{db}] TO DISK = N'{backup}' WITH INIT, CHECKSUM, FORMAT");
        SqlServerDatabaseFixture.ExecuteScript(fixture.MasterConnectionString,
            $"RESTORE VERIFYONLY FROM DISK = N'{backup}' WITH CHECKSUM");
        var before = RowCounts(fixture.ConnectionString);

        Migrate(fixture.ConnectionString);
        SqlServerDatabaseFixture.ExecuteScript(fixture.ConnectionString,
            "INSERT INTO AdsDesigns (Name, UserId, DesignData, LastModified) VALUES (N'After upgrade', NEWID(), N'{}', SYSUTCDATETIME())");
        Assert.Equal(before["AdsDesigns"] + 1, RowCounts(fixture.ConnectionString)["AdsDesigns"]);

        SqlConnection.ClearAllPools();
        SqlServerDatabaseFixture.ExecuteScript(fixture.MasterConnectionString,
            $"ALTER DATABASE [{db}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE");
        SqlServerDatabaseFixture.ExecuteScript(fixture.MasterConnectionString,
            $"RESTORE DATABASE [{db}] FROM DISK = N'{backup}' WITH REPLACE, CHECKSUM");
        SqlServerDatabaseFixture.ExecuteScript(fixture.MasterConnectionString,
            $"ALTER DATABASE [{db}] SET MULTI_USER");

        // The pre-upgrade state: no history table, the original rows, the legacy default back.
        Assert.Equal(0, SqlServerDatabaseFixture.Scalar<int>(fixture.ConnectionString,
            "SELECT COUNT(*) FROM sys.tables WHERE name = N'__EFMigrationsHistory'"));
        Assert.Equal(before, RowCounts(fixture.ConnectionString));
        Assert.Null(SqlServerDatabaseFixture.Scalar<int?>(fixture.ConnectionString,
            "SELECT COL_LENGTH(N'AdsPlanApprovals', N'RevertPlanJson')"));

        // And it can be migrated again.
        Migrate(fixture.ConnectionString);
        Assert.Equal(before, RowCounts(fixture.ConnectionString));
    }

    private static ServiceProvider BuildServices(string connectionString, params (string Key, string Value)[] settings)
    {
        var values = new Dictionary<string, string?>
        {
            ["Database:Provider"] = "SqlServer",
            ["ConnectionStrings:ads-main"] = connectionString,
        };
        foreach (var (key, value) in settings)
            values[key] = value;

        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        DatabaseProvider.AddDesignDbContext(services, DatabaseProvider.Resolve(configuration));
        return services.BuildServiceProvider();
    }

    [SqlServerFact]
    public void Bootstrapper_RefusesAMissingDatabaseUnlessAllowCreate()
    {
        using var fixture = new SqlServerDatabaseFixture();
        var missing = "ads_t_missing_" + Guid.NewGuid().ToString("N");
        var connectionString = fixture.ConnectionStringFor(missing);
        try
        {
            using (var refused = BuildServices(connectionString))
            {
                var ex = Assert.Throws<InvalidOperationException>(() => DatabaseBootstrapper.Initialize(refused));
                Assert.Contains(DatabaseBootstrapper.AllowCreateKey, ex.Message);
            }
            Assert.Equal(0, SqlServerDatabaseFixture.Scalar<int>(fixture.MasterConnectionString,
                $"SELECT COUNT(*) FROM sys.databases WHERE name = N'{missing}'"));

            using (var allowed = BuildServices(connectionString, (DatabaseBootstrapper.AllowCreateKey, "true")))
                DatabaseBootstrapper.Initialize(allowed);

            Assert.Equal(1, SqlServerDatabaseFixture.Scalar<int>(fixture.MasterConnectionString,
                $"SELECT COUNT(*) FROM sys.databases WHERE name = N'{missing}'"));
            Assert.True(SqlServerDatabaseFixture.Scalar<int>(connectionString,
                "SELECT COUNT(*) FROM __EFMigrationsHistory") >= 1);
        }
        finally
        {
            fixture.DropDatabase(missing);
        }
    }

    [SqlServerFact]
    public void Bootstrapper_RefusesPendingMigrationsWhenMigrateIsFalse()
    {
        using var fixture = new SqlServerDatabaseFixture();

        using (var refused = BuildServices(fixture.ConnectionString, (DatabaseBootstrapper.MigrateKey, "false")))
        {
            var ex = Assert.Throws<InvalidOperationException>(() => DatabaseBootstrapper.Initialize(refused));
            Assert.Contains(DatabaseBootstrapper.MigrateKey, ex.Message);
        }

        Migrate(fixture.ConnectionString);

        using var allowed = BuildServices(fixture.ConnectionString, (DatabaseBootstrapper.MigrateKey, "false"));
        DatabaseBootstrapper.Initialize(allowed);
    }
}
