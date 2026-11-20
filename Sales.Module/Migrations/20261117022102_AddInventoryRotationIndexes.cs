using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddInventoryRotationIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Paso 15 (S7): DDL idempotente; un reintento no falla si el indice ya existe.
            migrationBuilder.Sql(
                """
                CREATE INDEX IF NOT EXISTS "IX_Sales_Date_SaleType_InvoiceNumber"
                    ON "Sales" ("Date", "SaleType", "InvoiceNumber");

                CREATE INDEX IF NOT EXISTS "IX_SaleItems_ProductId_SaleId"
                    ON "SaleItems" ("ProductId", "SaleId");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS "IX_Sales_Date_SaleType_InvoiceNumber";
                DROP INDEX IF EXISTS "IX_SaleItems_ProductId_SaleId";
                """);
        }
    }
}
