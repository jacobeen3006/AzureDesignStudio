using AzureDesignStudio.Services.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace AzureDesignStudio.Server.Persistence;

/// <summary>
/// Brings the server database schema up once at startup. The only caller of
/// <c>EnsureCreated</c> in Server and Services; nothing calls it on a request path.
/// </summary>
internal static class DatabaseBootstrapper
{
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

        db.Database.EnsureCreated();

        // EnsureCreated creates a brand-new database with the full schema, and does nothing at
        // all for one that already exists - it never adds a missing table. The guarded DDL is
        // what brings a database created by an earlier release up to date. The DDL is T-SQL,
        // which SQLite rejects, so the guard is the SQL Server check, not the generic relational
        // one. Phase 4 replaces this method with migrations.
        if (NeedsLegacyUpgrade(db.Database))
            db.Database.ExecuteSqlRaw(LegacySqlServerUpgrade);
    }

    /// <summary>True when the legacy T-SQL upgrade applies: the SQL Server provider and no other.
    /// A separate method so a test can assert the gate without a live server.</summary>
    internal static bool NeedsLegacyUpgrade(DatabaseFacade database) => database.IsSqlServer();

    internal const string LegacySqlServerUpgrade = """
        IF OBJECT_ID(N'AdsPlanApprovals', N'U') IS NULL
        CREATE TABLE AdsPlanApprovals (
            PlanHash nvarchar(71) NOT NULL PRIMARY KEY,
            PlanJson nvarchar(max) NOT NULL,
            Status nvarchar(20) NOT NULL,
            ApprovedBy nvarchar(320) NOT NULL,
            ApprovedByUserId nvarchar(64) NOT NULL,
            ApprovedAtUtc datetime2 NOT NULL DEFAULT GETUTCDATE(),
            RevertPlanJson nvarchar(max) NULL);
        IF OBJECT_ID(N'AdsPlanApplyEvents', N'U') IS NULL
        CREATE TABLE AdsPlanApplyEvents (
            Id int IDENTITY(1,1) NOT NULL PRIMARY KEY,
            ApplyId uniqueidentifier NOT NULL,
            PlanHash nvarchar(71) NOT NULL,
            PlanJson nvarchar(max) NOT NULL,
            SubscriptionId nvarchar(64) NOT NULL,
            ResourceGroupName nvarchar(90) NOT NULL,
            TargetScope nvarchar(300) NULL,
            DeploymentName nvarchar(64) NOT NULL,
            ApprovedBy nvarchar(320) NOT NULL,
            ApprovedByUserId nvarchar(64) NOT NULL,
            ApprovedAtUtc datetime2 NOT NULL,
            PreviewEvidence nvarchar(64) NULL,
            RevertPlanHash nvarchar(71) NULL,
            RevertPlanJson nvarchar(max) NULL,
            EventType nvarchar(16) NOT NULL,
            Detail nvarchar(1024) NULL,
            TimestampUtc datetime2 NOT NULL);
        IF OBJECT_ID(N'AdsPlanApplyEvents', N'U') IS NOT NULL AND INDEXPROPERTY(OBJECT_ID(N'AdsPlanApplyEvents'), N'IX_AdsPlanApplyEvents_ApplyId', 'IndexID') IS NULL
        CREATE INDEX IX_AdsPlanApplyEvents_ApplyId ON AdsPlanApplyEvents (ApplyId);
        IF OBJECT_ID(N'AdsPlanApprovals', N'U') IS NOT NULL AND COL_LENGTH(N'AdsPlanApprovals', N'RevertPlanJson') IS NULL
        ALTER TABLE AdsPlanApprovals ADD RevertPlanJson nvarchar(max) NULL;
        IF OBJECT_ID(N'AdsPlanApplyEvents', N'U') IS NOT NULL AND COL_LENGTH(N'AdsPlanApplyEvents', N'RevertPlanHash') IS NULL
        ALTER TABLE AdsPlanApplyEvents ADD RevertPlanHash nvarchar(71) NULL;
        IF OBJECT_ID(N'AdsPlanApplyEvents', N'U') IS NOT NULL AND COL_LENGTH(N'AdsPlanApplyEvents', N'RevertPlanJson') IS NULL
        ALTER TABLE AdsPlanApplyEvents ADD RevertPlanJson nvarchar(max) NULL;
        IF OBJECT_ID(N'AdsPlanApplyEvents', N'U') IS NOT NULL AND COL_LENGTH(N'AdsPlanApplyEvents', N'TargetScope') IS NULL
        ALTER TABLE AdsPlanApplyEvents ADD TargetScope nvarchar(300) NULL;
        IF OBJECT_ID(N'AdsGuardrailPolicies', N'U') IS NULL
        CREATE TABLE AdsGuardrailPolicies (
            Id int NOT NULL PRIMARY KEY,
            Json nvarchar(max) NOT NULL,
            UpdatedBy nvarchar(320) NOT NULL,
            UpdatedByUserId nvarchar(64) NOT NULL,
            UpdatedAtUtc datetime2 NOT NULL);
        IF OBJECT_ID(N'AdsDecomApprovals', N'U') IS NULL
        CREATE TABLE AdsDecomApprovals (
            ExecutionHash nvarchar(71) NOT NULL PRIMARY KEY,
            WindowId nvarchar(64) NOT NULL,
            PlanHash nvarchar(71) NOT NULL,
            SubscriptionId nvarchar(64) NOT NULL,
            ApprovedBy nvarchar(320) NOT NULL,
            ApprovedByUserId nvarchar(64) NOT NULL,
            ApprovedAtUtc datetime2 NOT NULL);
        IF OBJECT_ID(N'AdsDecomWindowEvents', N'U') IS NULL
        CREATE TABLE AdsDecomWindowEvents (
            Id uniqueidentifier NOT NULL PRIMARY KEY,
            ApplyId uniqueidentifier NOT NULL,
            Event nvarchar(16) NOT NULL,
            WindowId nvarchar(64) NOT NULL,
            PlanHash nvarchar(71) NOT NULL,
            ExecutionHash nvarchar(71) NOT NULL,
            SubscriptionId nvarchar(64) NOT NULL,
            Principal nvarchar(320) NOT NULL,
            Detail nvarchar(1024) NULL,
            AtUtc datetime2 NOT NULL);
        IF OBJECT_ID(N'AdsDecomWindowEvents', N'U') IS NOT NULL AND INDEXPROPERTY(OBJECT_ID(N'AdsDecomWindowEvents'), N'IX_AdsDecomWindowEvents_ApplyId', 'IndexID') IS NULL
        CREATE INDEX IX_AdsDecomWindowEvents_ApplyId ON AdsDecomWindowEvents (ApplyId);
        """;
}
