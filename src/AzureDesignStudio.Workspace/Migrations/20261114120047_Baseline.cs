using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureDesignStudio.Workspace.Migrations
{
    /// <summary>
    /// The history every workspace.db carries. Its Up is guarded SQL (IF NOT EXISTS on every table
    /// and index) so a database that shipped before migrations existed adopts it without losing a
    /// row; the generated Designer and model snapshot describe the model as usual.
    /// </summary>
    public partial class Baseline : Migration
    {
        // The DDL EF emits for the model, rewritten with IF NOT EXISTS. Tables first (Scans before
        // ResourceSnapshots, which references it), then indexes. SQLite has no conditional
        // ADD COLUMN, so the two later Scans columns are added by LegacyAdoption before this runs.
        internal const string GuardedDdl = """
            CREATE TABLE IF NOT EXISTS "AuditRecords" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_AuditRecords" PRIMARY KEY AUTOINCREMENT,
                "Timestamp" TEXT NOT NULL,
                "Actor" TEXT NOT NULL,
                "Action" TEXT NOT NULL,
                "Target" TEXT NOT NULL,
                "PlanHash" TEXT NULL,
                "DetailJson" TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS "ChangeFeed" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ChangeFeed" PRIMARY KEY AUTOINCREMENT,
                "ScanId" TEXT NULL,
                "ChangeKey" TEXT NOT NULL,
                "ChangeTime" TEXT NOT NULL,
                "TargetResourceId" TEXT NOT NULL,
                "TargetResourceType" TEXT NULL,
                "ChangeType" TEXT NOT NULL,
                "CorrelationId" TEXT NULL,
                "ChangedBy" TEXT NULL,
                "ChangedByType" TEXT NULL,
                "ClientType" TEXT NULL,
                "Operation" TEXT NULL,
                "ChangesCount" INTEGER NOT NULL,
                "ChangedPropertiesJson" TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS "CostCache" (
                "Key" TEXT NOT NULL CONSTRAINT "PK_CostCache" PRIMARY KEY,
                "Json" TEXT NOT NULL,
                "FetchedAt" TEXT NOT NULL,
                "ExpiresAt" TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS "DecomWindows" (
                "WindowId" TEXT NOT NULL CONSTRAINT "PK_DecomWindows" PRIMARY KEY,
                "ScanId" TEXT NOT NULL,
                "SubscriptionId" TEXT NOT NULL,
                "PlanHash" TEXT NOT NULL,
                "ExecutionHash" TEXT NOT NULL,
                "ScopesJson" TEXT NOT NULL,
                "State" TEXT NOT NULL,
                "RowsJson" TEXT NULL,
                "UpdatedAtUtc" TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS "Designs" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Designs" PRIMARY KEY,
                "Name" TEXT NOT NULL,
                "Data" TEXT NOT NULL,
                "SavedAt" TEXT NOT NULL,
                "SyncedAt" TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS "ExecutionPlans" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_ExecutionPlans" PRIMARY KEY,
                "DesignName" TEXT NOT NULL,
                "Json" TEXT NOT NULL,
                "PlanHash" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "CreatedAt" TEXT NOT NULL,
                "ApprovedBy" TEXT NULL,
                "ApprovedAt" TEXT NULL,
                "ExecutedAt" TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS "GovernanceFindings" (
                "ScanId" TEXT NOT NULL,
                "Source" TEXT NOT NULL,
                "RecordId" TEXT NOT NULL,
                "ResourceId" TEXT NOT NULL,
                "SubscriptionId" TEXT NOT NULL,
                "Severity" TEXT NOT NULL,
                "Category" TEXT NOT NULL,
                "Title" TEXT NOT NULL,
                "Detail" TEXT NULL,
                "PolicyAssignmentId" TEXT NULL,
                "PolicyDefinitionId" TEXT NULL,
                CONSTRAINT "PK_GovernanceFindings" PRIMARY KEY ("ScanId", "Source", "RecordId")
            );

            CREATE TABLE IF NOT EXISTS "NodePositions" (
                "NodeId" TEXT NOT NULL CONSTRAINT "PK_NodePositions" PRIMARY KEY,
                "ContainerId" TEXT NOT NULL,
                "X" REAL NOT NULL,
                "Y" REAL NOT NULL,
                "SavedAt" TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS "Scans" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_Scans" PRIMARY KEY,
                "Scope" TEXT NOT NULL,
                "ScopeType" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "StartedAt" TEXT NOT NULL,
                "FinishedAt" TEXT NULL,
                "ContentHash" TEXT NULL,
                "ResourceCount" INTEGER NOT NULL,
                "Error" TEXT NULL,
                "ManifestJson" TEXT NULL,
                "CheckpointJson" TEXT NULL
            );

            CREATE TABLE IF NOT EXISTS "ResourceSnapshots" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_ResourceSnapshots" PRIMARY KEY AUTOINCREMENT,
                "ResourceId" TEXT NOT NULL,
                "ResourceType" TEXT NOT NULL,
                "ScanId" TEXT NOT NULL,
                "Name" TEXT NOT NULL,
                "Location" TEXT NULL,
                "ResourceGroup" TEXT NULL,
                "SubscriptionId" TEXT NULL,
                "TagsJson" TEXT NULL,
                "PropertiesJson" TEXT NULL,
                "TakenAt" TEXT NOT NULL,
                CONSTRAINT "FK_ResourceSnapshots_Scans_ScanId" FOREIGN KEY ("ScanId") REFERENCES "Scans" ("Id") ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS "IX_AuditRecords_Timestamp" ON "AuditRecords" ("Timestamp");

            CREATE INDEX IF NOT EXISTS "IX_ChangeFeed_ChangeTime" ON "ChangeFeed" ("ChangeTime");

            CREATE INDEX IF NOT EXISTS "IX_ChangeFeed_ScanId" ON "ChangeFeed" ("ScanId");

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_ChangeFeed_TargetResourceId_ChangeKey" ON "ChangeFeed" ("TargetResourceId", "ChangeKey");

            CREATE INDEX IF NOT EXISTS "IX_CostCache_ExpiresAt" ON "CostCache" ("ExpiresAt");

            CREATE INDEX IF NOT EXISTS "IX_DecomWindows_SubscriptionId_WindowId" ON "DecomWindows" ("SubscriptionId", "WindowId");

            CREATE UNIQUE INDEX IF NOT EXISTS "IX_Designs_Name" ON "Designs" ("Name");

            CREATE INDEX IF NOT EXISTS "IX_Designs_SyncedAt" ON "Designs" ("SyncedAt");

            CREATE INDEX IF NOT EXISTS "IX_ExecutionPlans_PlanHash" ON "ExecutionPlans" ("PlanHash");

            CREATE INDEX IF NOT EXISTS "IX_ExecutionPlans_Status" ON "ExecutionPlans" ("Status");

            CREATE INDEX IF NOT EXISTS "IX_GovernanceFindings_ScanId_ResourceId" ON "GovernanceFindings" ("ScanId", "ResourceId");

            CREATE INDEX IF NOT EXISTS "IX_ResourceSnapshots_ResourceId_TakenAt" ON "ResourceSnapshots" ("ResourceId", "TakenAt");

            CREATE INDEX IF NOT EXISTS "IX_ResourceSnapshots_ScanId_ResourceId" ON "ResourceSnapshots" ("ScanId", "ResourceId");

            CREATE INDEX IF NOT EXISTS "IX_Scans_Scope_StartedAt" ON "Scans" ("Scope", "StartedAt");
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(GuardedDdl);

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore-from-backup is the supported rollback (phase CONTEXT), never Down.
        }
    }
}
