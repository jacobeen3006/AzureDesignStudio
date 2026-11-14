using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureDesignStudio.Server.Persistence.Migrations
{
    /// <summary>
    /// Keys plan and decommission approvals by (scope, hash) and records who requested a plan
    /// approval's preview. Per table the sequence is: add <c>TenantId</c> as nullable, backfill it with
    /// the empty GUID (pending approvals fail closed, D-16), make it NOT NULL, drop the old
    /// single-column primary key (looked up by name) and add the composite one. Every statement is
    /// guarded, so a database that already has any step is not touched twice and no row is lost, and
    /// each is its own command because a statement naming a column added in the same batch does not
    /// compile. No default constraint is added: the model knows none. The generated Designer and
    /// snapshot describe the model; only <c>Up</c> was written by hand.
    /// </summary>
    public partial class TenantApprovals : Migration
    {
        internal const string AddPlanTenantIdSql = """
            IF OBJECT_ID(N'AdsPlanApprovals', N'U') IS NOT NULL AND COL_LENGTH(N'AdsPlanApprovals', N'TenantId') IS NULL
            ALTER TABLE AdsPlanApprovals ADD TenantId uniqueidentifier NULL;
            """;

        internal const string AddPlanRequestedByUserIdSql = """
            IF OBJECT_ID(N'AdsPlanApprovals', N'U') IS NOT NULL AND COL_LENGTH(N'AdsPlanApprovals', N'RequestedByUserId') IS NULL
            ALTER TABLE AdsPlanApprovals ADD RequestedByUserId nvarchar(64) NULL;
            """;

        // Approvals pending at upgrade go to the empty-GUID scope, which no caller has: they fail closed
        // and must be approved again (D-16). An UPDATE, not a column default - the model knows no default.
        internal const string BackfillPlanTenantIdSql = """
            IF OBJECT_ID(N'AdsPlanApprovals', N'U') IS NOT NULL
            UPDATE AdsPlanApprovals SET TenantId = '00000000-0000-0000-0000-000000000000' WHERE TenantId IS NULL;
            """;

        internal const string RequirePlanTenantIdSql = """
            IF OBJECT_ID(N'AdsPlanApprovals', N'U') IS NOT NULL AND COLUMNPROPERTY(OBJECT_ID(N'AdsPlanApprovals'), N'TenantId', 'AllowsNull') = 1
            ALTER TABLE AdsPlanApprovals ALTER COLUMN TenantId uniqueidentifier NOT NULL;
            """;

        // The old key is found by name: a database that predates migrations carries a system-generated one.
        // EXEC accepts string concatenation but not a function call, so the text is built first.
        internal const string DropPlanOldKeySql = """
            IF OBJECT_ID(N'AdsPlanApprovals', N'U') IS NOT NULL
            BEGIN
                DECLARE @keyName sysname = (
                    SELECT kc.name
                    FROM sys.key_constraints kc
                    WHERE kc.parent_object_id = OBJECT_ID(N'AdsPlanApprovals') AND kc.type = 'PK'
                      AND NOT EXISTS (
                        SELECT 1
                        FROM sys.index_columns ic
                        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                        WHERE ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id AND c.name = N'TenantId'));
                IF @keyName IS NOT NULL
                BEGIN
                    DECLARE @drop nvarchar(400) = N'ALTER TABLE AdsPlanApprovals DROP CONSTRAINT ' + QUOTENAME(@keyName);
                    EXEC(@drop);
                END
            END
            """;

        internal const string AddPlanKeySql = """
            IF OBJECT_ID(N'AdsPlanApprovals', N'U') IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'AdsPlanApprovals') AND type = 'PK')
            ALTER TABLE AdsPlanApprovals ADD CONSTRAINT PK_AdsPlanApprovals PRIMARY KEY (TenantId, PlanHash);
            """;

        internal const string AddDecomTenantIdSql = """
            IF OBJECT_ID(N'AdsDecomApprovals', N'U') IS NOT NULL AND COL_LENGTH(N'AdsDecomApprovals', N'TenantId') IS NULL
            ALTER TABLE AdsDecomApprovals ADD TenantId uniqueidentifier NULL;
            """;

        // Approvals pending at upgrade go to the empty-GUID scope, which no caller has: they fail closed
        // and must be approved again (D-16). An UPDATE, not a column default - the model knows no default.
        internal const string BackfillDecomTenantIdSql = """
            IF OBJECT_ID(N'AdsDecomApprovals', N'U') IS NOT NULL
            UPDATE AdsDecomApprovals SET TenantId = '00000000-0000-0000-0000-000000000000' WHERE TenantId IS NULL;
            """;

        internal const string RequireDecomTenantIdSql = """
            IF OBJECT_ID(N'AdsDecomApprovals', N'U') IS NOT NULL AND COLUMNPROPERTY(OBJECT_ID(N'AdsDecomApprovals'), N'TenantId', 'AllowsNull') = 1
            ALTER TABLE AdsDecomApprovals ALTER COLUMN TenantId uniqueidentifier NOT NULL;
            """;

        // The old key is found by name: a database that predates migrations carries a system-generated one.
        // EXEC accepts string concatenation but not a function call, so the text is built first.
        internal const string DropDecomOldKeySql = """
            IF OBJECT_ID(N'AdsDecomApprovals', N'U') IS NOT NULL
            BEGIN
                DECLARE @keyName sysname = (
                    SELECT kc.name
                    FROM sys.key_constraints kc
                    WHERE kc.parent_object_id = OBJECT_ID(N'AdsDecomApprovals') AND kc.type = 'PK'
                      AND NOT EXISTS (
                        SELECT 1
                        FROM sys.index_columns ic
                        JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                        WHERE ic.object_id = kc.parent_object_id AND ic.index_id = kc.unique_index_id AND c.name = N'TenantId'));
                IF @keyName IS NOT NULL
                BEGIN
                    DECLARE @drop nvarchar(400) = N'ALTER TABLE AdsDecomApprovals DROP CONSTRAINT ' + QUOTENAME(@keyName);
                    EXEC(@drop);
                END
            END
            """;

        internal const string AddDecomKeySql = """
            IF OBJECT_ID(N'AdsDecomApprovals', N'U') IS NOT NULL
               AND NOT EXISTS (SELECT 1 FROM sys.key_constraints WHERE parent_object_id = OBJECT_ID(N'AdsDecomApprovals') AND type = 'PK')
            ALTER TABLE AdsDecomApprovals ADD CONSTRAINT PK_AdsDecomApprovals PRIMARY KEY (TenantId, ExecutionHash);
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AddPlanTenantIdSql);
            migrationBuilder.Sql(AddPlanRequestedByUserIdSql);
            migrationBuilder.Sql(BackfillPlanTenantIdSql);
            migrationBuilder.Sql(RequirePlanTenantIdSql);
            migrationBuilder.Sql(DropPlanOldKeySql);
            migrationBuilder.Sql(AddPlanKeySql);

            migrationBuilder.Sql(AddDecomTenantIdSql);
            migrationBuilder.Sql(BackfillDecomTenantIdSql);
            migrationBuilder.Sql(RequireDecomTenantIdSql);
            migrationBuilder.Sql(DropDecomOldKeySql);
            migrationBuilder.Sql(AddDecomKeySql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: the supported rollback is restoring the pre-upgrade backup.
        }
    }
}
