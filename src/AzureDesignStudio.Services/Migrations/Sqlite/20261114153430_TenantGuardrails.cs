using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureDesignStudio.Services.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class TenantGuardrails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AdsGuardrailPolicies",
                type: "TEXT",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AdsSecurityEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "TEXT", nullable: false),
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    EventType = table.Column<string>(type: "TEXT", maxLength: 32, nullable: false),
                    Principal = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    PrincipalUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    Detail = table.Column<string>(type: "TEXT", maxLength: 1024, nullable: true),
                    AtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdsSecurityEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AdsTenantSettings",
                columns: table => new
                {
                    TenantId = table.Column<Guid>(type: "TEXT", nullable: false),
                    AllowSelfApproval = table.Column<bool>(type: "INTEGER", nullable: false),
                    GuardrailPolicyMode = table.Column<string>(type: "TEXT", maxLength: 16, nullable: false),
                    UpdatedBy = table.Column<string>(type: "TEXT", maxLength: 320, nullable: false),
                    UpdatedByUserId = table.Column<string>(type: "TEXT", maxLength: 64, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "TEXT", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AdsTenantSettings", x => x.TenantId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AdsGuardrailPolicies_TenantId",
                table: "AdsGuardrailPolicies",
                column: "TenantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AdsSecurityEvents_TenantId",
                table: "AdsSecurityEvents",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AdsSecurityEvents");

            migrationBuilder.DropTable(
                name: "AdsTenantSettings");

            migrationBuilder.DropIndex(
                name: "IX_AdsGuardrailPolicies_TenantId",
                table: "AdsGuardrailPolicies");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AdsGuardrailPolicies");
        }
    }
}
