using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddSupplierInvoiceLineOcrConfidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "OcrNameConfidence",
                table: "SupplierInvoiceLines",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OcrQuantityConfidence",
                table: "SupplierInvoiceLines",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OcrUnitCostConfidence",
                table: "SupplierInvoiceLines",
                type: "numeric(5,2)",
                precision: 5,
                scale: 2,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "OcrNameConfidence",
                table: "SupplierInvoiceLines");

            migrationBuilder.DropColumn(
                name: "OcrQuantityConfidence",
                table: "SupplierInvoiceLines");

            migrationBuilder.DropColumn(
                name: "OcrUnitCostConfidence",
                table: "SupplierInvoiceLines");
        }
    }
}
