using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureDesignStudio.Server.Persistence.Migrations
{
    /// <summary>
    /// The history every server database carries. Its Up is guarded T-SQL (IF OBJECT_ID on every
    /// table, COL_LENGTH on every later column, INDEXPROPERTY on every index) so a database that
    /// shipped before migrations existed adopts it without a table being recreated or a row
    /// lost; the generated Designer and model snapshot describe the model as usual.
    /// </summary>
    public partial class Baseline : Migration
    {
        // AdsDesigns and AzureSubscriptions are EF's generated CREATE TABLE text behind an
        // existence guard. The five tables below them are the startup DDL earlier releases ran
        // (DatabaseBootstrapper.LegacySqlServerUpgrade), moved here; only their CREATE branches
        // changed: named primary keys, and no default on AdsPlanApprovals.ApprovedAtUtc, so a
        // database this baseline creates matches the model. A table that already exists is
        // skipped by its guard and keeps whatever names it was created with.
        internal const string GuardedSql = """
            IF OBJECT_ID(N'AdsDesigns', N'U') IS NULL
            CREATE TABLE AdsDesigns (
                Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AdsDesigns PRIMARY KEY,
                Name nvarchar(200) NOT NULL,
                UserId uniqueidentifier NOT NULL,
                DesignData nvarchar(max) NOT NULL,
                LastModified datetime2 NOT NULL,
                CONSTRAINT ADS_CHECK_JSON CHECK (ISJSON(DesignData)=1));
            IF OBJECT_ID(N'AzureSubscriptions', N'U') IS NULL
            CREATE TABLE AzureSubscriptions (
                Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AzureSubscriptions PRIMARY KEY,
                UserId uniqueidentifier NOT NULL,
                SubscriptionId uniqueidentifier NOT NULL,
                SubscriptionName nvarchar(64) NOT NULL,
                TenantId uniqueidentifier NOT NULL,
                ClientId uniqueidentifier NOT NULL,
                ClientSecret nvarchar(max) NOT NULL);
            IF OBJECT_ID(N'AdsPlanApprovals', N'U') IS NULL
            CREATE TABLE AdsPlanApprovals (
                PlanHash nvarchar(71) NOT NULL CONSTRAINT PK_AdsPlanApprovals PRIMARY KEY,
                PlanJson nvarchar(max) NOT NULL,
                Status nvarchar(20) NOT NULL,
                ApprovedBy nvarchar(320) NOT NULL,
                ApprovedByUserId nvarchar(64) NOT NULL,
                ApprovedAtUtc datetime2 NOT NULL,
                RevertPlanJson nvarchar(max) NULL);
            IF OBJECT_ID(N'AdsPlanApplyEvents', N'U') IS NULL
            CREATE TABLE AdsPlanApplyEvents (
                Id int IDENTITY(1,1) NOT NULL CONSTRAINT PK_AdsPlanApplyEvents PRIMARY KEY,
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
                Id int NOT NULL CONSTRAINT PK_AdsGuardrailPolicies PRIMARY KEY,
                Json nvarchar(max) NOT NULL,
                UpdatedBy nvarchar(320) NOT NULL,
                UpdatedByUserId nvarchar(64) NOT NULL,
                UpdatedAtUtc datetime2 NOT NULL);
            IF OBJECT_ID(N'AdsDecomApprovals', N'U') IS NULL
            CREATE TABLE AdsDecomApprovals (
                ExecutionHash nvarchar(71) NOT NULL CONSTRAINT PK_AdsDecomApprovals PRIMARY KEY,
                WindowId nvarchar(64) NOT NULL,
                PlanHash nvarchar(71) NOT NULL,
                SubscriptionId nvarchar(64) NOT NULL,
                ApprovedBy nvarchar(320) NOT NULL,
                ApprovedByUserId nvarchar(64) NOT NULL,
                ApprovedAtUtc datetime2 NOT NULL);
            IF OBJECT_ID(N'AdsDecomWindowEvents', N'U') IS NULL
            CREATE TABLE AdsDecomWindowEvents (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_AdsDecomWindowEvents PRIMARY KEY,
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

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(GuardedSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: the supported rollback is restoring the pre-upgrade backup.
        }
    }
}
