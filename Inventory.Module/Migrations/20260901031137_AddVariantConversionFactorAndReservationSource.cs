using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddVariantConversionFactorAndReservationSource : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SourceProductId",
                table: "StockReservations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "ConversionFactor",
                table: "Products",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 1.0000m);

            migrationBuilder.CreateIndex(
                name: "IX_StockReservations_SourceProductId",
                table: "StockReservations",
                column: "SourceProductId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Products_ConversionFactor",
                table: "Products",
                sql: "\"ConversionFactor\" > 0");

            migrationBuilder.AddForeignKey(
                name: "FK_StockReservations_Products_SourceProductId",
                table: "StockReservations",
                column: "SourceProductId",
                principalTable: "Products",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_StockReservations_Products_SourceProductId",
                table: "StockReservations");

            migrationBuilder.DropIndex(
                name: "IX_StockReservations_SourceProductId",
                table: "StockReservations");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Products_ConversionFactor",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "SourceProductId",
                table: "StockReservations");

            migrationBuilder.DropColumn(
                name: "ConversionFactor",
                table: "Products");
        }
    }
}
