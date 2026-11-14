using AzureDesignStudio.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

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
}
