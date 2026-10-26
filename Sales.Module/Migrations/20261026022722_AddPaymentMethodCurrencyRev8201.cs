using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <summary>
    /// 8.201: moneda persistida de la gaveta por método de pago. La columna nace nullable para
    /// no romper filas/inserts legados; el backfill aplica la regla histórica (nombre contiene
    /// 'USD' -> USD, resto -> Bs.S) y desde entonces un renombre no reclasifica movimientos.
    /// Idempotente por guard de information_schema.
    /// </summary>
    public partial class AddPaymentMethodCurrencyRev8201 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'public' AND table_name = 'PaymentMethods' AND column_name = 'Currency') THEN
                        ALTER TABLE "PaymentMethods" ADD COLUMN "Currency" character varying(10) NULL;
                        RAISE NOTICE '[8.201] PaymentMethods.Currency agregada.';
                    END IF;
                END $$;
                """);

            // Backfill único: la moneda histórica se deriva del nombre SOLO para filas sin dato.
            // Las filas nuevas las escribe PaymentMethodService con la moneda resuelta.
            migrationBuilder.Sql(
                """
                UPDATE "PaymentMethods"
                SET "Currency" = CASE WHEN "Name" ILIKE '%USD%' THEN 'USD' ELSE 'Bs.S' END
                WHERE "Currency" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM information_schema.columns
                               WHERE table_schema = 'public' AND table_name = 'PaymentMethods' AND column_name = 'Currency') THEN
                        ALTER TABLE "PaymentMethods" DROP COLUMN "Currency";
                    END IF;
                END $$;
                """);
        }
    }
}
