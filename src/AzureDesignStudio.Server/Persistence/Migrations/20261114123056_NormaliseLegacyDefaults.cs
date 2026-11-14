using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureDesignStudio.Server.Persistence.Migrations
{
    /// <summary>
    /// Removes the one difference between a database that shipped before migrations and one the
    /// baseline created: an unnamed <c>DEFAULT GETUTCDATE()</c> on <c>AdsPlanApprovals.ApprovedAtUtc</c>
    /// that the model does not know and that would block a later column alteration. The model did
    /// not change, so the snapshot is untouched; only <c>Up</c> was written by hand.
    /// </summary>
    public partial class NormaliseLegacyDefaults : Migration
    {
        // A no-op on a database the baseline created (it has no such default).
        internal const string Sql = """
            DECLARE @defaultName sysname = (
                SELECT d.name
                FROM sys.default_constraints d
                JOIN sys.columns c ON c.object_id = d.parent_object_id AND c.column_id = d.parent_column_id
                WHERE d.parent_object_id = OBJECT_ID(N'AdsPlanApprovals') AND c.name = N'ApprovedAtUtc');
            IF @defaultName IS NOT NULL
            BEGIN
                -- EXEC accepts string concatenation but not a function call, so build the text first.
                DECLARE @drop nvarchar(400) = N'ALTER TABLE AdsPlanApprovals DROP CONSTRAINT ' + QUOTENAME(@defaultName);
                EXEC(@drop);
            END
            """;

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(Sql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Intentionally empty: the supported rollback is restoring the pre-upgrade backup.
        }
    }
}
