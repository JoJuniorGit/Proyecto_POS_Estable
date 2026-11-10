using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <summary>
    /// 8.188-H3 (cierre del Paso 13, S5): vinculo del gasto con el supervisor que aprobo su
    /// anulacion. Aditiva sobre bases pobladas: la columna nullable
    /// `CancellationAuthorizedByUserId`, su indice y su FK RESTRICT a `Users` se agregan con
    /// guards `information_schema.columns` y `pg_constraint`, sin cambios destructivos. Down es
    /// espejo y seguro (FK -> indice -> columna), con las mismas guardas de idempotencia.
    /// </summary>
    public partial class AddPettyCashCancellationAuthorizerRev8188 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF to_regclass('"PettyCashExpenses"') IS NOT NULL THEN
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'PettyCashExpenses' AND column_name = 'CancellationAuthorizedByUserId') THEN
                            ALTER TABLE "PettyCashExpenses" ADD COLUMN IF NOT EXISTS "CancellationAuthorizedByUserId" integer NULL;
                        END IF;

                        CREATE INDEX IF NOT EXISTS "IX_PettyCashExpenses_CancellationAuthorizedByUserId"
                            ON "PettyCashExpenses" ("CancellationAuthorizedByUserId");

                        IF NOT EXISTS (SELECT 1 FROM pg_constraint
                                       WHERE conname = 'FK_PettyCashExpenses_Users_CancellationAuthorizedByUserId') THEN
                            ALTER TABLE "PettyCashExpenses"
                                ADD CONSTRAINT "FK_PettyCashExpenses_Users_CancellationAuthorizedByUserId"
                                FOREIGN KEY ("CancellationAuthorizedByUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT;
                        END IF;

                        RAISE NOTICE '[8.188-H3] Supervisor aprobador de anulacion garantizado (idempotente).';
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
                        IF EXISTS (SELECT 1 FROM pg_constraint
                                   WHERE conname = 'FK_PettyCashExpenses_Users_CancellationAuthorizedByUserId') THEN
                            ALTER TABLE "PettyCashExpenses"
                                DROP CONSTRAINT "FK_PettyCashExpenses_Users_CancellationAuthorizedByUserId";
                        END IF;

                        DROP INDEX IF EXISTS "IX_PettyCashExpenses_CancellationAuthorizedByUserId";
                        ALTER TABLE "PettyCashExpenses" DROP COLUMN IF EXISTS "CancellationAuthorizedByUserId";
                    END IF;
                END $$;
                """);
        }
    }
}
