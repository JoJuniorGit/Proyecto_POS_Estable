using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddCashTransactionCurrencySnapshotRev8211 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CurrencyCode",
                table: "CashTransactions",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            // 8.211-T3b3 (S6): backfill idempotente de las filas legacy con la misma regla de la
            // escritura. Caja chica y su reversa usan la moneda del gasto; venta efectiva (Income),
            // abono CxC y reembolso usan la moneda persistida del metodo; el resto es moneda local.
            // Solo completa el snapshot: nunca recalcula montos, saldos ni tasas historicas.
            migrationBuilder.Sql(
                """
                UPDATE "CashTransactions" AS ct
                SET "CurrencyCode" = CASE
                    WHEN ct."PettyCashExpenseId" IS NOT NULL THEN COALESCE(
                        (SELECT CASE WHEN pce."Currency" = 1 THEN 'USD' ELSE 'Bs.S' END
                         FROM "PettyCashExpenses" AS pce
                         WHERE pce."Id" = ct."PettyCashExpenseId"),
                        'Bs.S')
                    WHEN ct."Source" IN (1, 7, 8)
                         AND NOT (ct."Source" = 1 AND ct."Type" = 1)
                         AND ct."PaymentMethodId" IS NOT NULL THEN COALESCE(
                        (SELECT CASE
                                    WHEN UPPER(TRIM(pm."Currency")) = 'USD' THEN 'USD'
                                    WHEN UPPER(TRIM(pm."Currency")) IN ('VES', 'BSS', 'BS.S') THEN 'Bs.S'
                                    WHEN NULLIF(TRIM(pm."Currency"), '') IS NULL AND pm."Name" ILIKE '%USD%' THEN 'USD'
                                    ELSE 'Bs.S'
                                END
                         FROM "PaymentMethods" AS pm
                         WHERE pm."Id" = ct."PaymentMethodId"),
                        'Bs.S')
                    ELSE 'Bs.S'
                END
                WHERE ct."CurrencyCode" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrencyCode",
                table: "CashTransactions");
        }
    }
}
