using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureDesignStudio.Server.Persistence.Migrations
{
    /// <summary>
    /// Stores guardrail policies per tenant scope: a nullable <c>TenantId</c> on
    /// <c>AdsGuardrailPolicies</c> (the legacy Id=1 row keeps NULL and stays the inherited default),
    /// a filtered unique index on it, and the new <c>AdsTenantSettings</c> and <c>AdsSecurityEvents</c>
    /// tables. Every statement is guarded (<c>COL_LENGTH</c>, <c>INDEXPROPERTY</c>, <c>OBJECT_ID</c>), so
    /// a database that already has any part of it is not touched twice and no row is lost. No default
    /// constraint is added: the model knows none. The generated Designer and snapshot describe the model;
    /// only <c>Up</c> was written by hand.
    /// </summary>
    public partial class TenantGuardrails : Migration
    {
        internal const string AddTenantIdColumnSql = """
            IF OBJECT_ID(N'AdsGuardrailPolicies', N'U') IS NOT NULL AND COL_LENGTH(N'AdsGuardrailPolicies', N'TenantId') IS NULL
            ALTER TABLE AdsGuardrailPolicies ADD TenantId uniqueidentifier NULL;
            """;

        // A separate command from the column add: inside one batch the index statement could not
        // compile against a column the same batch adds.
        internal const string CreateTenantIdIndexSql = """
            IF OBJECT_ID(N'AdsGuardrailPolicies', N'U') IS NOT NULL AND INDEXPROPERTY(OBJECT_ID(N'AdsGuardrailPolicies'), N'IX_AdsGuardrailPolicies_TenantId', 'IndexID') IS NULL
            CREATE UNIQUE INDEX IX_AdsGuardrailPolicies_TenantId ON AdsGuardrailPolicies (TenantId) WHERE TenantId IS NOT NULL;
            """;

        internal const string CreateTenantSettingsSql = """
            IF OBJECT_ID(N'AdsTenantSettings', N'U') IS NULL
            CREATE TABLE AdsTenantSettings (
                TenantId uniqueidentifier NOT NULL CONSTRAINT PK_AdsTenantSettings PRIMARY KEY,
                AllowSelfApproval bit NOT NULL,
                GuardrailPolicyMode nvarchar(16) NOT NULL,
                UpdatedBy nvarchar(320) NOT NULL,
                UpdatedByUserId nvarchar(64) NOT NULL,
                UpdatedAtUtc datetime2 NOT NULL);
            """;

        internal const string CreateSecurityEventsSql = """
            IF OBJECT_ID(N'AdsSecurityEvents', N'U') IS NULL
            CREATE TABLE AdsSecurityEvents (
                Id uniqueidentifier NOT NULL CONSTRAINT PK_AdsSecurityEvents PRIMARY KEY,
                TenantId uniqueidentifier NOT NULL,
                EventType nvarchar(32) NOT NULL,
                Principal nvarchar(320) NOT NULL,
                PrincipalUserId nvarchar(64) NOT NULL,
                Detail nvarchar(1024) NULL,
                AtUtc datetime2 NOT NULL);
            """;

        internal const string CreateSecurityEventsIndexSql = """
            IF OBJECT_ID(N'AdsSecurityEvents', N'U') IS NOT NULL AND INDEXPROPERTY(OBJECT_ID(N'AdsSecurityEvents'), N'IX_AdsSecurityEvents_TenantId', 'IndexID') IS NULL
            CREATE INDEX IX_AdsSecurityEvents_TenantId ON AdsSecurityEvents (TenantId);
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AddTenantIdColumnSql);
            migrationBuilder.Sql(CreateTenantIdIndexSql);
            migrationBuilder.Sql(CreateTenantSettingsSql);
            migrationBuilder.Sql(CreateSecurityEventsSql);
            migrationBuilder.Sql(CreateSecurityEventsIndexSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: the supported rollback is restoring the pre-upgrade backup.
        }
    }
}
