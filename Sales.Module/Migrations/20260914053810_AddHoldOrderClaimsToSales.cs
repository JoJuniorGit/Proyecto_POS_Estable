using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddHoldOrderClaimsToSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClaimAction",
                table: "Sales",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "ClaimedAtUtc",
                table: "Sales",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ClaimedByUserId",
                table: "Sales",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClaimedByUserName",
                table: "Sales",
                type: "character varying(150)",
                maxLength: 150,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ClaimAction",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "ClaimedAtUtc",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "ClaimedByUserId",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "ClaimedByUserName",
                table: "Sales");
        }
    }
}
