using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AzureDesignStudio.Services.Migrations.Sqlite
{
    /// <inheritdoc />
    public partial class TenantApprovals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_AdsPlanApprovals",
                table: "AdsPlanApprovals");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AdsDecomApprovals",
                table: "AdsDecomApprovals");

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AdsPlanApprovals",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "RequestedByUserId",
                table: "AdsPlanApprovals",
                type: "TEXT",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "TenantId",
                table: "AdsDecomApprovals",
                type: "TEXT",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddPrimaryKey(
                name: "PK_AdsPlanApprovals",
                table: "AdsPlanApprovals",
                columns: new[] { "TenantId", "PlanHash" });

            migrationBuilder.AddPrimaryKey(
                name: "PK_AdsDecomApprovals",
                table: "AdsDecomApprovals",
                columns: new[] { "TenantId", "ExecutionHash" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropPrimaryKey(
                name: "PK_AdsPlanApprovals",
                table: "AdsPlanApprovals");

            migrationBuilder.DropPrimaryKey(
                name: "PK_AdsDecomApprovals",
                table: "AdsDecomApprovals");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AdsPlanApprovals");

            migrationBuilder.DropColumn(
                name: "RequestedByUserId",
                table: "AdsPlanApprovals");

            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AdsDecomApprovals");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AdsPlanApprovals",
                table: "AdsPlanApprovals",
                column: "PlanHash");

            migrationBuilder.AddPrimaryKey(
                name: "PK_AdsDecomApprovals",
                table: "AdsDecomApprovals",
                column: "ExecutionHash");
        }
    }
}
