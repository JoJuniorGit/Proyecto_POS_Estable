using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddSaleIdToStockMovement : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 8.16-H03: clave estructurada de idempotencia de la deducción (SaleId), independiente
            // del string Reason. Nullable: movimientos no derivados de venta o históricos.
            migrationBuilder.AddColumn<int>(
                name: "SaleId",
                table: "StockMovements",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_SaleId",
                table: "StockMovements",
                column: "SaleId");

            // 8.16-H03: el archivo conserva la misma clave estructurada (auditoría).
            migrationBuilder.AddColumn<int>(
                name: "SaleId",
                table: "StockMovements_Archive",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SaleId",
                table: "StockMovements_Archive");

            migrationBuilder.DropIndex(
                name: "IX_StockMovements_SaleId",
                table: "StockMovements");

            migrationBuilder.DropColumn(
                name: "SaleId",
                table: "StockMovements");
        }
    }
}