using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <summary>
    /// 8.142: elimina el esquema huerfano de la feature "adelantos de cliente" (tablas
    /// CustomerAdvances y SaleCustomerAdvancePayments, columna Sales.IsAdvanceConsumption y su
    /// indice parcial) que ya no tiene ninguna referencia en el codigo. Verificado antes de
    /// eliminar: 0 referencias en codigo, 0 FKs entrantes, 0 filas en SaleCustomerAdvancePayments
    /// y 0 ventas con IsAdvanceConsumption = true. La unica fila de CustomerAdvances (dato de
    /// prueba de 2026-08-13) quedo respaldada en backup-customer-advances-20260919.sql, fuera del
    /// repo: Down() no es reversible por diseno y el backup es la via de recuperacion.
    /// </summary>
    public partial class DropOrphanAdvanceSchemaRev8142 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS "SaleCustomerAdvancePayments";
                DROP TABLE IF EXISTS "CustomerAdvances";
                DROP INDEX IF EXISTS "IX_Sales_CustomerId_IsAdvanceConsumption_Status";
                ALTER TABLE "Sales" DROP COLUMN IF EXISTS "IsAdvanceConsumption";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Irreversible: el dato respaldado vive fuera del repo. Recrear las tablas vacias no
            // aportaria nada, asi que se deja constancia explicita en lugar de un Down enganoso.
        }
    }
}
