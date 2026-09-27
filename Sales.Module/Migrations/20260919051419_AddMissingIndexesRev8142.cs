using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <summary>
    /// 8.142: crea los indices que el modelo y el snapshot ya declaraban pero que ninguna
    /// migracion emitia (IX_Sales_Status_Date, IX_Sales_Status_DeliveryStatus,
    /// IX_Customers_Name, IX_Customers_Active_Name, IX_CashTransactions_TransactionTime y
    /// IX_CashTransactions_SessionId_TransactionTime). Consecuencia del drift: la app
    /// arrancaba sin PendingModelChangesWarning y los tests los tenian por EnsureCreated,
    /// pero una base migrada no. Idempotente (CREATE INDEX IF NOT EXISTS + guard to_regclass)
    /// para tolerar bases ya reparadas a mano y para no fallar si una tabla no existe.
    /// </summary>
    public partial class AddMissingIndexesRev8142 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF to_regclass('"Sales"') IS NOT NULL THEN
                        CREATE INDEX IF NOT EXISTS "IX_Sales_Status_Date" ON "Sales" ("Status", "Date");
                        CREATE INDEX IF NOT EXISTS "IX_Sales_Status_DeliveryStatus" ON "Sales" ("Status", "DeliveryStatus");
                    END IF;

                    IF to_regclass('"Customers"') IS NOT NULL THEN
                        CREATE INDEX IF NOT EXISTS "IX_Customers_Name" ON "Customers" ("Name");
                        CREATE INDEX IF NOT EXISTS "IX_Customers_Active_Name" ON "Customers" ("IsActive", "Name") WHERE "IsActive" = true;
                    END IF;

                    IF to_regclass('"CashTransactions"') IS NOT NULL THEN
                        CREATE INDEX IF NOT EXISTS "IX_CashTransactions_TransactionTime" ON "CashTransactions" ("TransactionTime");
                        CREATE INDEX IF NOT EXISTS "IX_CashTransactions_SessionId_TransactionTime" ON "CashTransactions" ("SessionId", "TransactionTime");
                    END IF;

                    RAISE NOTICE '[8.142] Indices faltantes de Sales/Customers/CashTransactions verificados.';
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP INDEX IF EXISTS "IX_Sales_Status_Date";
                DROP INDEX IF EXISTS "IX_Sales_Status_DeliveryStatus";
                DROP INDEX IF EXISTS "IX_Customers_Name";
                DROP INDEX IF EXISTS "IX_Customers_Active_Name";
                DROP INDEX IF EXISTS "IX_CashTransactions_TransactionTime";
                DROP INDEX IF EXISTS "IX_CashTransactions_SessionId_TransactionTime";
                """);
        }
    }
}
