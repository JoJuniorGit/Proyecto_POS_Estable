using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddVariantStockAndPricingFlags : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasIndependentPricing",
                table: "Products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsStockShared",
                table: "Products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddCheckConstraint(
                name: "CK_Products_Variant_Flags",
                table: "Products",
                sql: "(\"IsGroupHeader\" = TRUE AND \"ParentProductId\" IS NULL) OR (\"IsStockShared\" = FALSE AND \"HasIndependentPricing\" = FALSE)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Products_Variant_Flags",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "HasIndependentPricing",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "IsStockShared",
                table: "Products");
        }
    }
}
