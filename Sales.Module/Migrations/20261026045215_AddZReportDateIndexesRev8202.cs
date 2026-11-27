using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddZReportDateIndexesRev8202 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 8.202-H4 (S8): migración idempotente. CREATE INDEX IF NOT EXISTS permite reaplicar
            // sobre bases donde el índice ya fue creado manualmente sin romper el despliegue.
            migrationBuilder.Sql("""
                CREATE INDEX IF NOT EXISTS "IX_SalePayments_CreatedAt"
                    ON "SalePayments" ("CreatedAt");

                CREATE INDEX IF NOT EXISTS "IX_ReceivablePayments_ReceivedAtUtc"
                    ON "ReceivablePayments" ("ReceivedAtUtc");

                CREATE INDEX IF NOT EXISTS "IX_PettyCashExpenses_CreatedAtUtc"
                    ON "PettyCashExpenses" ("CreatedAtUtc");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DROP INDEX IF EXISTS "IX_SalePayments_CreatedAt";
                DROP INDEX IF EXISTS "IX_ReceivablePayments_ReceivedAtUtc";
                DROP INDEX IF EXISTS "IX_PettyCashExpenses_CreatedAtUtc";
                """);
        }
    }
}