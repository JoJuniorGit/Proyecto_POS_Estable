using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <summary>
    /// Residual 8.187-8.188: arqueo fisico USD del cierre diario. Aditiva y segura sobre bases
    /// pobladas: tres columnas nullable numeric(18,4) se agregan con guards de
    /// `information_schema.columns`, sin defaults inventados ni cambios destructivos. Down es
    /// espejo (DROP COLUMN IF EXISTS) con la misma guarda de idempotencia.
    /// </summary>
    public partial class AddDailyClosureUsdCountRev8188 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF to_regclass('"DailyClosures"') IS NOT NULL THEN
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'DailyClosures' AND column_name = 'ExpectedClosingBalanceUsd') THEN
                            ALTER TABLE "DailyClosures" ADD COLUMN IF NOT EXISTS "ExpectedClosingBalanceUsd" numeric(18,4) NULL;
                        END IF;

                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'DailyClosures' AND column_name = 'ActualClosingBalanceUsd') THEN
                            ALTER TABLE "DailyClosures" ADD COLUMN IF NOT EXISTS "ActualClosingBalanceUsd" numeric(18,4) NULL;
                        END IF;

                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'DailyClosures' AND column_name = 'DifferenceUsd') THEN
                            ALTER TABLE "DailyClosures" ADD COLUMN IF NOT EXISTS "DifferenceUsd" numeric(18,4) NULL;
                        END IF;

                        RAISE NOTICE '[8.187-8.188] Arqueo USD del cierre diario garantizado (idempotente).';
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
                    IF to_regclass('"DailyClosures"') IS NOT NULL THEN
                        ALTER TABLE "DailyClosures" DROP COLUMN IF EXISTS "ExpectedClosingBalanceUsd";
                        ALTER TABLE "DailyClosures" DROP COLUMN IF EXISTS "ActualClosingBalanceUsd";
                        ALTER TABLE "DailyClosures" DROP COLUMN IF EXISTS "DifferenceUsd";
                    END IF;
                END $$;
                """);
        }
    }
}
