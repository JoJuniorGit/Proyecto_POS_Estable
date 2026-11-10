using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddSaleMonetaryProfileSnapshotRev8211 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MonetaryMode",
                table: "Sales",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Dual");

            migrationBuilder.AddColumn<string>(
                name: "PrimaryCurrencyCode",
                table: "Sales",
                type: "character varying(3)",
                maxLength: 3,
                nullable: false,
                defaultValue: "USD");

            migrationBuilder.AddColumn<string>(
                name: "SecondaryCurrencyCode",
                table: "Sales",
                type: "character varying(3)",
                maxLength: 3,
                nullable: true,
                defaultValue: "VES");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MonetaryMode",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "PrimaryCurrencyCode",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "SecondaryCurrencyCode",
                table: "Sales");
        }
    }
}
