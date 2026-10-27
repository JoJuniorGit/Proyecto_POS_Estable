using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddSalePaymentCurrencySnapshotRev8211 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CurrencyCode",
                table: "SalePayments",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            // 8.211-T3b1 (S6/S8): backfill idempotente de las filas legacy. La autoridad es la
            // moneda persistida del método; el nombre solo resuelve filas sin Currency. Nunca
            // recalcula montos ni tasas: solo completa el snapshot aditivo de la moneda.
            // La columna admite 10 caracteres porque la moneda local canónica es "Bs.S".
            migrationBuilder.Sql(
                """
                UPDATE "SalePayments" AS sp
                SET "CurrencyCode" = COALESCE(
                    (SELECT CASE
                                WHEN NULLIF(TRIM(pm."Currency"), '') IS NULL THEN
                                    CASE WHEN pm."Name" ILIKE '%USD%' THEN 'USD' ELSE 'Bs.S' END
                                WHEN UPPER(TRIM(pm."Currency")) IN ('VES', 'BSS') THEN 'Bs.S'
                                WHEN UPPER(TRIM(pm."Currency")) = 'USD' THEN 'USD'
                                ELSE TRIM(pm."Currency")
                            END
                     FROM "PaymentMethods" AS pm
                     WHERE pm."Id" = sp."PaymentMethodId"),
                    'Bs.S')
                WHERE sp."CurrencyCode" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "CurrencyCode",
                table: "SalePayments");
        }
    }
}
