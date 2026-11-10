using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <summary>
    /// Paso 14 (F1-WU2): backfill correctivo de la comision de servicio de los avances historicos.
    /// WU1 no pudo poblarla porque el flujo de avance creaba los movimientos de caja sin
    /// <c>CashTransactions.SaleId</c>. Este barrido empareja cada avance con UNA pareja unica
    /// egreso fisico (capital) + ingreso no fisico (comision) dentro de una ventana de 10 minutos,
    /// exige la misma sesion y la misma moneda en ambos movimientos y en el pago de la venta, y
    /// valida que capital + comision igualen el total local del pago. Los avances con cruce de
    /// monedas entre capital y comision se dejan deliberadamente en cero: se reportan como
    /// desconocidos/cobertura y nunca se adivinan. Solo toca avances con comision cero: nunca
    /// reescribe mercancia ni ventas ya backfilleadas.
    /// </summary>
    public partial class CorrectCashAdvanceCommissionBackfillRev8213 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                WITH candidates AS (
                    SELECT
                        s."Id" AS "SaleId",
                        income."AmountUsd" AS "CommissionUsd",
                        income."AmountLocal" AS "CommissionLocal",
                        COUNT(*) OVER (PARTITION BY s."Id") AS "CandidateCount"
                    FROM "Sales" AS s
                    JOIN "SalePayments" AS sp ON sp."SaleId" = s."Id"
                    JOIN "CashTransactions" AS expense
                      ON expense."Source" = 2
                     AND expense."Type" = 1
                     AND expense."IsPhysicalCash" = true
                     AND expense."SaleId" IS NULL
                     AND expense."TransactionTime" BETWEEN s."Date" - interval '10 minutes' AND s."Date" + interval '10 minutes'
                    JOIN "CashTransactions" AS income
                      ON income."Source" = 2
                     AND income."Type" = 0
                     AND income."IsPhysicalCash" = false
                     AND income."SaleId" IS NULL
                     AND income."PaymentMethodId" = sp."PaymentMethodId"
                     AND income."TransactionTime" BETWEEN s."Date" - interval '10 minutes' AND s."Date" + interval '10 minutes'
                     AND expense."SessionId" = income."SessionId"
                     AND expense."CurrencyCode" IS NOT DISTINCT FROM income."CurrencyCode"
                     AND income."CurrencyCode" IS NOT DISTINCT FROM sp."CurrencyCode"
                    WHERE s."SaleType" = 1
                      AND s."CommissionAmountLocal" = 0
                      AND ROUND(expense."AmountLocal" + income."AmountLocal", 2) = ROUND(sp."AmountBsS", 2)
                )
                UPDATE "Sales" AS s
                SET "CommissionAmountUSD" = c."CommissionUsd",
                    "CommissionAmountLocal" = c."CommissionLocal"
                FROM candidates AS c
                WHERE s."Id" = c."SaleId"
                  AND c."CandidateCount" = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Correccion de datos historicos: no es reversible sin inventar los valores previos.
            // El rollback del esquema no requiere accion (no hay DDL).
        }
    }
}
