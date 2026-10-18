using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <summary>
    /// 8.188-T1 (Paso 13, S2/S6): dominio y persistencia de la caja chica bimonetaria.
    /// Aditiva sobre bases pobladas: la gaveta dual de <c>CashDrawerSessions</c> se agrega con
    /// guards `information_schema.columns` y defaults seguros (USD 0; el cierre queda en 0 para
    /// filas existentes hasta que el turno se cierre), la secuencia `petty_cash_voucher_seq` y la
    /// tabla `PettyCashExpenses` usan `CREATE ... IF NOT EXISTS` con FKs RESTRICT, y el vinculo
    /// `CashTransactions.PettyCashExpenseId` se agrega con `ADD COLUMN IF NOT EXISTS` + guard de
    /// constraint. Sin cambios destructivos. Down es espejo y seguro (`DROP IF EXISTS`).
    /// </summary>
    public partial class AddPettyCashExpensesRev8188 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1) Gaveta dual de la sesion: apertura USD NOT NULL DEFAULT 0 y cierre nullable con
            // DEFAULT 0 solo para filas existentes (la migracion no inventa un conteo de cierre).
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF to_regclass('"CashDrawerSessions"') IS NOT NULL THEN
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'CashDrawerSessions' AND column_name = 'OpeningBalanceUsd') THEN
                            ALTER TABLE "CashDrawerSessions" ADD COLUMN "OpeningBalanceUsd" numeric(18,4) NOT NULL DEFAULT 0;
                        END IF;

                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'CashDrawerSessions' AND column_name = 'ClosingBalanceUsd') THEN
                            ALTER TABLE "CashDrawerSessions" ADD COLUMN "ClosingBalanceUsd" numeric(18,4) NULL DEFAULT 0;
                        END IF;
                    END IF;

                    RAISE NOTICE '[8.188-T1] Gaveta dual USD de CashDrawerSessions garantizada (idempotente).';
                END $$;
                """);

            // 2) Secuencia dedicada del correlativo del vale (VCC-000001) y tabla del gasto.
            migrationBuilder.Sql(
                """
                CREATE SEQUENCE IF NOT EXISTS "petty_cash_voucher_seq" START 1;

                CREATE TABLE IF NOT EXISTS "PettyCashExpenses" (
                    "Id" serial PRIMARY KEY,
                    "VoucherNumber" bigint NOT NULL DEFAULT nextval('petty_cash_voucher_seq'),
                    "SessionId" integer NOT NULL,
                    "Category" integer NOT NULL,
                    "Currency" integer NOT NULL,
                    "AmountBsS" numeric(18,2) NOT NULL,
                    "AmountUsd" numeric(18,4) NOT NULL,
                    "HistoricalRate" numeric(18,4) NOT NULL,
                    "Concept" character varying(500) NOT NULL,
                    "Beneficiary" character varying(150) NOT NULL,
                    "ReferenceNumber" character varying(100) NOT NULL,
                    "CreatedByUserId" integer NOT NULL,
                    "CreatedAtUtc" timestamp with time zone NOT NULL,
                    "AuthorizedByUserId" integer NULL,
                    "AuthorizationRequestId" integer NULL,
                    "Status" integer NOT NULL,
                    "CancelledAtUtc" timestamp with time zone NULL,
                    "CancelledByUserId" integer NULL,
                    "CancellationReason" character varying(500) NULL,
                    CONSTRAINT "FK_PettyCashExpenses_AuthorizationRequests_AuthorizationRequestId" FOREIGN KEY ("AuthorizationRequestId") REFERENCES "AuthorizationRequests" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_PettyCashExpenses_CashDrawerSessions_SessionId" FOREIGN KEY ("SessionId") REFERENCES "CashDrawerSessions" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_PettyCashExpenses_Users_AuthorizedByUserId" FOREIGN KEY ("AuthorizedByUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_PettyCashExpenses_Users_CancelledByUserId" FOREIGN KEY ("CancelledByUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_PettyCashExpenses_Users_CreatedByUserId" FOREIGN KEY ("CreatedByUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
                );

                CREATE UNIQUE INDEX IF NOT EXISTS "IX_PettyCashExpenses_VoucherNumber"
                    ON "PettyCashExpenses" ("VoucherNumber");

                CREATE INDEX IF NOT EXISTS "IX_PettyCashExpenses_SessionId"
                    ON "PettyCashExpenses" ("SessionId");

                CREATE INDEX IF NOT EXISTS "IX_PettyCashExpenses_SessionId_Status"
                    ON "PettyCashExpenses" ("SessionId", "Status");

                CREATE INDEX IF NOT EXISTS "IX_PettyCashExpenses_CreatedByUserId"
                    ON "PettyCashExpenses" ("CreatedByUserId");

                CREATE INDEX IF NOT EXISTS "IX_PettyCashExpenses_AuthorizedByUserId"
                    ON "PettyCashExpenses" ("AuthorizedByUserId");

                CREATE INDEX IF NOT EXISTS "IX_PettyCashExpenses_CancelledByUserId"
                    ON "PettyCashExpenses" ("CancelledByUserId");

                CREATE INDEX IF NOT EXISTS "IX_PettyCashExpenses_AuthorizationRequestId"
                    ON "PettyCashExpenses" ("AuthorizationRequestId");
                """);

            // 3) Vinculo del libro transaccional con el gasto (Expense original y reversa): la FK
            // vive aqui para evitar el ciclo entre PettyCashExpenses y CashTransactions.
            migrationBuilder.Sql(
                """
                ALTER TABLE "CashTransactions" ADD COLUMN IF NOT EXISTS "PettyCashExpenseId" integer NULL;

                CREATE INDEX IF NOT EXISTS "IX_CashTransactions_PettyCashExpenseId"
                    ON "CashTransactions" ("PettyCashExpenseId");

                DO $$
                BEGIN
                    IF to_regclass('"CashTransactions"') IS NOT NULL
                       AND NOT EXISTS (SELECT 1 FROM pg_constraint
                                       WHERE conname = 'FK_CashTransactions_PettyCashExpenses_PettyCashExpenseId') THEN
                        ALTER TABLE "CashTransactions"
                            ADD CONSTRAINT "FK_CashTransactions_PettyCashExpenses_PettyCashExpenseId"
                            FOREIGN KEY ("PettyCashExpenseId") REFERENCES "PettyCashExpenses" ("Id") ON DELETE RESTRICT;
                    END IF;

                    RAISE NOTICE '[8.188-T1] Vinculo CashTransactions.PettyCashExpenseId garantizado (idempotente).';
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS "PettyCashExpenses";
                DROP SEQUENCE IF EXISTS "petty_cash_voucher_seq";

                DO $$
                BEGIN
                    IF to_regclass('"CashTransactions"') IS NOT NULL THEN
                        ALTER TABLE "CashTransactions" DROP COLUMN IF EXISTS "PettyCashExpenseId";
                    END IF;

                    IF to_regclass('"CashDrawerSessions"') IS NOT NULL THEN
                        ALTER TABLE "CashDrawerSessions" DROP COLUMN IF EXISTS "ClosingBalanceUsd";
                        ALTER TABLE "CashDrawerSessions" DROP COLUMN IF EXISTS "OpeningBalanceUsd";
                    END IF;
                END $$;
                """);
        }
    }
}
