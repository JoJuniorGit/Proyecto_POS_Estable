using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    public partial class AlignCashTransactionCurrencyBackfillRev8211 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 8.211-T6-U6: the original 8.211 backfill mapped any non-USD/non-VES persisted
            // method currency to Bs.S. Correct only rows whose snapshot is still the legacy
            // fallback and whose persisted method currency is a valid ISO code. Amounts, rates
            // and balances are untouched; this is a metadata correction.
            migrationBuilder.Sql(
                """
                UPDATE "CashTransactions" AS ct
                SET "CurrencyCode" = UPPER(TRIM(pm."Currency"))
                FROM "PaymentMethods" AS pm
                WHERE ct."CurrencyCode" = 'Bs.S'
                  AND ct."PaymentMethodId" = pm."Id"
                  AND ct."Source" IN (1, 7, 8)
                  AND NOT (ct."Source" = 1 AND ct."Type" = 1)
                  AND UPPER(TRIM(pm."Currency")) ~ '^[A-Z]{3}$'
                  AND UPPER(TRIM(pm."Currency")) NOT IN ('USD', 'VES', 'BSS', 'BS.S');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Metadata correction is intentionally irreversible: reverting would recreate the
            // known wrong Bs.S fallback. Historical amounts were never modified.
        }
    }
}
