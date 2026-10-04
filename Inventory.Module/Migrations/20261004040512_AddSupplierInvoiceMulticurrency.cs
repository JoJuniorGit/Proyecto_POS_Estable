using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierInvoiceMulticurrency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AppliedRate",
                table: "SupplierInvoices",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 1m);

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "SupplierInvoices",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "USD");

            migrationBuilder.AddColumn<decimal>(
                name: "UnitCostDocument",
                table: "SupplierInvoiceLines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            // 8.146-D11: las facturas existentes son de la era USD; el costo documental de arranque
            // es el costo normalizado ya persistido.
            migrationBuilder.Sql(
                "UPDATE \"SupplierInvoiceLines\" SET \"UnitCostDocument\" = \"UnitCostUSD\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppliedRate",
                table: "SupplierInvoices");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "SupplierInvoices");

            migrationBuilder.DropColumn(
                name: "UnitCostDocument",
                table: "SupplierInvoiceLines");
        }
    }
}
