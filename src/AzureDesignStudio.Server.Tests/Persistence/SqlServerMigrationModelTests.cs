using AzureDesignStudio.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;
using TenantGuardrails = AzureDesignStudio.Server.Persistence.Migrations.TenantGuardrails;

namespace AzureDesignStudio.Server.Tests.Persistence;

/// <summary>
/// Offline checks of the SQL Server migration set. The design-time factory builds the same model the
/// server runs on SQL Server without opening a connection, so none of this needs a database.
/// </summary>
public sealed class SqlServerMigrationModelTests
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
        "AdsTenantSettings",
        "AdsSecurityEvents",
    ];

    [Fact]
    public void SqlServerMigrationModelHasNoPendingChanges()
    {
        using var db = new DesignDbContextFactory().CreateDbContext([]);

        // The snapshot and the model agree: a model edit without a new migration fails here.
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void SqlServerMigrationModelStartsWithTheBaseline()
    {
        using var db = new DesignDbContextFactory().CreateDbContext([]);

        // Later plans append migrations, so only the first id is pinned.
        var first = db.Database.GetMigrations().First();
        Assert.EndsWith("_Baseline", first);
    }

    [Fact]
    public void SqlServerMigrationScriptGuardsEveryTableAndKeepsTheJsonCheck()
    {
        using var db = new DesignDbContextFactory().CreateDbContext([]);

        // Scripting is offline: no connection is opened.
        var script = db.GetService<IMigrator>().GenerateScript();

        foreach (var table in Tables)
            Assert.Contains($"OBJECT_ID(N'{table}'", script);
        Assert.Contains("ADS_CHECK_JSON", script);
    }

    [Fact]
    public void TenantGuardrailsScriptGuardsTheColumnTheIndexAndTheTables()
    {
        using var db = new DesignDbContextFactory().CreateDbContext([]);

        var script = db.GetService<IMigrator>().GenerateScript();

        Assert.Contains("COL_LENGTH(N'AdsGuardrailPolicies', N'TenantId') IS NULL", script);
        Assert.Contains("INDEXPROPERTY(OBJECT_ID(N'AdsGuardrailPolicies'), N'IX_AdsGuardrailPolicies_TenantId', 'IndexID') IS NULL", script);
        Assert.Contains("WHERE TenantId IS NOT NULL", script);
        Assert.Contains("CONSTRAINT PK_AdsTenantSettings PRIMARY KEY", script);
        Assert.Contains("CONSTRAINT PK_AdsSecurityEvents PRIMARY KEY", script);
        // The model knows no default for any column, so this migration must leave none behind.
        foreach (var text in new[]
                 {
                     TenantGuardrails.AddTenantIdColumnSql, TenantGuardrails.CreateTenantIdIndexSql,
                     TenantGuardrails.CreateTenantSettingsSql, TenantGuardrails.CreateSecurityEventsSql,
                     TenantGuardrails.CreateSecurityEventsIndexSql,
                 })
            Assert.DoesNotContain("DEFAULT", text, StringComparison.OrdinalIgnoreCase);
    }
}
