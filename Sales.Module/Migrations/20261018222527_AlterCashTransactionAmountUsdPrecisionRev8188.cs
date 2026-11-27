using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <summary>
    /// 8.188-H4 (cierre del Paso 13, S2): el libro de caja (CashTransactions.AmountUsd) pasa de
    /// numeric(18,2) a numeric(18,4) para conservar el cuarto decimal del USD nominal que escribe
    /// el alta de caja chica (el bucket fisico USD ya cuenta con 4dp). El cambio es idempotente:
    /// el guard `information_schema.columns` verifica data_type/numeric_precision/numeric_scale y
    /// solo altera la columna si aun no es numeric(18,4); jamas recrea ni dropea nada. Down
    /// revierte a numeric(18,2) con el mismo guard (la perdida del 4to decimal es inherente al
    /// downgrade, no un efecto colateral).
    /// </summary>
    public partial class AlterCashTransactionAmountUsdPrecisionRev8188 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'public' AND table_name = 'CashTransactions' AND column_name = 'AmountUsd'
                                     AND data_type = 'numeric' AND numeric_precision = 18 AND numeric_scale = 4) THEN
                        IF EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'public' AND table_name = 'CashTransactions' AND column_name = 'AmountUsd') THEN
                            ALTER TABLE "CashTransactions" ALTER COLUMN "AmountUsd" TYPE numeric(18,4);

                            RAISE NOTICE '[8.188-H4] CashTransactions.AmountUsd migrada a numeric(18,4) (idempotente).';
                        END IF;
                    END IF;
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'public' AND table_name = 'CashTransactions' AND column_name = 'AmountUsd'
                                     AND data_type = 'numeric' AND numeric_precision = 18 AND numeric_scale = 2) THEN
                        IF EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'public' AND table_name = 'CashTransactions' AND column_name = 'AmountUsd') THEN
                            ALTER TABLE "CashTransactions" ALTER COLUMN "AmountUsd" TYPE numeric(18,2);

                            RAISE NOTICE '[8.188-H4] CashTransactions.AmountUsd revertida a numeric(18,2) (idempotente).';
                        END IF;
                    END IF;
                END $$;
                """);
        }
    }
}
