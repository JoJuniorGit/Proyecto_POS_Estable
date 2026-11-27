using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <summary>
    /// Quick win 8.188 Paso 13 (S2): ancla de sesion del cierre diario para el renglon OPEX exacto.
    /// Aditiva sobre bases pobladas: la columna nullable `SessionId`, su indice y su FK RESTRICT a
    /// `CashDrawerSessions` se agregan con guards `information_schema.columns` y `pg_constraint`,
    /// sin cambios destructivos. Down es espejo y seguro (FK -> indice -> columna), con las mismas
    /// guardas de idempotencia.
    /// </summary>
    public partial class AddDailyClosurePettyCashSessionRev8188 : Migration
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
                                       WHERE table_schema = 'public' AND table_name = 'DailyClosures' AND column_name = 'SessionId') THEN
                            ALTER TABLE "DailyClosures" ADD COLUMN IF NOT EXISTS "SessionId" integer NULL;
                        END IF;

                        CREATE INDEX IF NOT EXISTS "IX_DailyClosures_SessionId"
                            ON "DailyClosures" ("SessionId");

                        IF NOT EXISTS (SELECT 1 FROM pg_constraint
                                       WHERE conname = 'FK_DailyClosures_CashDrawerSessions_SessionId') THEN
                            ALTER TABLE "DailyClosures"
                                ADD CONSTRAINT "FK_DailyClosures_CashDrawerSessions_SessionId"
                                FOREIGN KEY ("SessionId") REFERENCES "CashDrawerSessions" ("Id") ON DELETE RESTRICT;
                        END IF;

                        RAISE NOTICE '[8.188 quick win] Ancla de sesion del cierre garantizada (idempotente).';
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
                        IF EXISTS (SELECT 1 FROM pg_constraint
                                   WHERE conname = 'FK_DailyClosures_CashDrawerSessions_SessionId') THEN
                            ALTER TABLE "DailyClosures"
                                DROP CONSTRAINT "FK_DailyClosures_CashDrawerSessions_SessionId";
                        END IF;

                        DROP INDEX IF EXISTS "IX_DailyClosures_SessionId";
                        ALTER TABLE "DailyClosures" DROP COLUMN IF EXISTS "SessionId";
                    END IF;
                END $$;
                """);
        }
    }
}
