using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <summary>
    /// 8.142: agrega el snapshot de costo unitario a las lineas de venta (COGS historico para el
    /// Paso 14 de docs/Ideas.txt). Nullable: las lineas ya persistidas quedan en NULL (costo
    /// desconocido) en lugar de 0, para no reportar margen del 100% sobre el historico.
    /// </summary>
    public partial class AddSaleItemUnitCostSnapshotRev8142 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "UnitCostUSD",
                table: "SaleItems",
                type: "numeric(18,2)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "UnitCostUSD",
                table: "SaleItems");
        }
    }
}
