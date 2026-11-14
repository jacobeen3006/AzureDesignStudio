using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureDesignStudio.Services.Migrations.Sqlite
{
    /// <summary>
    /// The history every desktop <c>deploy.db</c> carries. Its Up is guarded SQL (CREATE ... IF NOT
    /// EXISTS) so a file that an earlier build created from the model adopts it without a table
    /// being recreated or a row lost; the generated Designer and model snapshot describe the model
    /// as usual. SQLite has no conditional ADD COLUMN, so the columns a very old file lacks are
    /// added by the desktop startup (DeployDbStartup) before this migration runs.
    /// </summary>
    public partial class Baseline : Migration
    {
        // Table and index definitions are exactly what `dotnet ef migrations script 0 Baseline`
        // prints for the model, with IF NOT EXISTS added to every statement.
        internal const string GuardedDdl = """
            CREATE TABLE IF NOT EXISTS "AdsDecomApprovals" (
                "ExecutionHash" TEXT NOT NULL CONSTRAINT "PK_AdsDecomApprovals" PRIMARY KEY,
                "WindowId" TEXT NOT NULL,
                "PlanHash" TEXT NOT NULL,
                "SubscriptionId" TEXT NOT NULL,
                "ApprovedBy" TEXT NOT NULL,
                "ApprovedByUserId" TEXT NOT NULL,
                "ApprovedAtUtc" TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS "AdsDecomWindowEvents" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_AdsDecomWindowEvents" PRIMARY KEY,
                "ApplyId" TEXT NOT NULL,
                "Event" TEXT NOT NULL,
                "WindowId" TEXT NOT NULL,
                "PlanHash" TEXT NOT NULL,
                "ExecutionHash" TEXT NOT NULL,
                "SubscriptionId" TEXT NOT NULL,
                "Principal" TEXT NOT NULL,
                "Detail" TEXT NULL,
                "AtUtc" TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS "AdsDesigns" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AdsDesigns" PRIMARY KEY AUTOINCREMENT,
                "Name" TEXT NOT NULL,
                "UserId" TEXT NOT NULL,
                "DesignData" TEXT NOT NULL,
                "LastModified" TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS "AdsGuardrailPolicies" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AdsGuardrailPolicies" PRIMARY KEY,
                "Json" TEXT NOT NULL,
                "UpdatedBy" TEXT NOT NULL,
                "UpdatedByUserId" TEXT NOT NULL,
                "UpdatedAtUtc" TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS "AdsPlanApplyEvents" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AdsPlanApplyEvents" PRIMARY KEY AUTOINCREMENT,
                "ApplyId" TEXT NOT NULL,
                "PlanHash" TEXT NOT NULL,
                "PlanJson" TEXT NOT NULL,
                "SubscriptionId" TEXT NOT NULL,
                "ResourceGroupName" TEXT NOT NULL,
                "TargetScope" TEXT NULL,
                "DeploymentName" TEXT NOT NULL,
                "ApprovedBy" TEXT NOT NULL,
                "ApprovedByUserId" TEXT NOT NULL,
                "ApprovedAtUtc" TEXT NOT NULL,
                "PreviewEvidence" TEXT NULL,
                "RevertPlanHash" TEXT NULL,
                "RevertPlanJson" TEXT NULL,
                "EventType" TEXT NOT NULL,
                "Detail" TEXT NULL,
                "TimestampUtc" TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS "AdsPlanApprovals" (
                "PlanHash" TEXT NOT NULL CONSTRAINT "PK_AdsPlanApprovals" PRIMARY KEY,
                "PlanJson" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "ApprovedBy" TEXT NOT NULL,
                "ApprovedByUserId" TEXT NOT NULL,
                "ApprovedAtUtc" TEXT NOT NULL,
                "RevertPlanJson" TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS "AzureSubscriptions" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AzureSubscriptions" PRIMARY KEY AUTOINCREMENT,
                "UserId" TEXT NOT NULL,
                "SubscriptionId" TEXT NOT NULL,
                "SubscriptionName" TEXT NOT NULL,
                "TenantId" TEXT NOT NULL,
                "ClientId" TEXT NOT NULL,
                "ClientSecret" TEXT NOT NULL
            );

            CREATE INDEX IF NOT EXISTS "IX_AdsDecomWindowEvents_ApplyId" ON "AdsDecomWindowEvents" ("ApplyId");

            CREATE INDEX IF NOT EXISTS "IX_AdsPlanApplyEvents_ApplyId" ON "AdsPlanApplyEvents" ("ApplyId");
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(GuardedDdl);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: rolling back means restoring the premigration backup taken
            // before this history existed; dropping the tables would delete approvals and audit events.
        }
    }
}
