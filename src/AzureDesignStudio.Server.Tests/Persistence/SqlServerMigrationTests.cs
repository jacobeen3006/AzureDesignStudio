using AzureDesignStudio.Server.Persistence;
using Microsoft.EntityFrameworkCore;
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
    }
}
