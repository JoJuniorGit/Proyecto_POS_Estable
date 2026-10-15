using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Module.Migrations
{
    /// <inheritdoc />
    public partial class AllowMultiProductSupplierCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SupplierProductCodes_Supplier_Code",
                table: "SupplierProductCodes");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierProductCodes_Supplier_Code",
                table: "SupplierProductCodes",
                columns: new[] { "SupplierId", "Code", "ProductId" },
                unique: true,
                filter: "\"Code\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SupplierProductCodes_Supplier_Code",
                table: "SupplierProductCodes");

            migrationBuilder.CreateIndex(
                name: "IX_SupplierProductCodes_Supplier_Code",
                table: "SupplierProductCodes",
                columns: new[] { "SupplierId", "Code" },
                unique: true,
                filter: "\"Code\" IS NOT NULL");
        }
    }
}
