using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddHasWholesaleToProducts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasWholesale",
                table: "Products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql(@"
                UPDATE ""Products""
                SET ""HasWholesale"" = TRUE
                WHERE ""PriceWholesaleUSD"" > 0 AND ""PriceWholesaleUSD"" <> ""PriceRetailUSD"";
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasWholesale",
                table: "Products");
        }
    }
}
