using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <summary>
    /// 8.188-T2a (Paso 13, S3): idempotencia del registro de gastos de caja chica. Aditiva sobre
    /// bases pobladas: la columna `IdempotencyKey` (varchar 100, nullable) y su indice unico
    /// filtrado (`WHERE "IdempotencyKey" IS NOT NULL`, NULL multiple permitido) se agregan con
    /// guards `to_regclass` + `IF NOT EXISTS`, sin cambios destructivos. Down es espejo y seguro.
    /// </summary>
    public partial class AddPettyCashIdempotencyRev8188 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF to_regclass('"PettyCashExpenses"') IS NOT NULL THEN
                        ALTER TABLE "PettyCashExpenses"
                            ADD COLUMN IF NOT EXISTS "IdempotencyKey" character varying(100) NULL;

                        CREATE UNIQUE INDEX IF NOT EXISTS "IX_PettyCashExpenses_IdempotencyKey"
                            ON "PettyCashExpenses" ("IdempotencyKey")
                            WHERE "IdempotencyKey" IS NOT NULL;

                        RAISE NOTICE '[8.188-T2a] Idempotencia de PettyCashExpenses garantizada (idempotente).';
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
                    IF to_regclass('"PettyCashExpenses"') IS NOT NULL THEN
                        DROP INDEX IF EXISTS "IX_PettyCashExpenses_IdempotencyKey";
                        ALTER TABLE "PettyCashExpenses" DROP COLUMN IF EXISTS "IdempotencyKey";
                    END IF;
                END $$;
                """);
        }
    }
}
