using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Module.Migrations
{
    /// <summary>
    /// 8.177-T1 (S1): datos de auditoría del Kardex en StockMovements y su espejo en
    /// StockMovements_Archive — OriginType, ReasonCode, AuthorizedByUserId, UnitCostUSD
    /// numeric(18,4), SupplierInvoiceId y Observation (todos nullable: los movimientos históricos
    /// quedan sin clasificar). Índices de consulta por ProductId y ReasonCode; el índice de
    /// ProductId de StockMovements ya existía por convención FK y solo se garantiza sin duplicarlo.
    /// El cuerpo usa guards to_regclass/information_schema, estilo 8.20-C01 (AddSaleIdToStockMovement):
    /// si la estructura ya existe la aplicación es un no-op y no provoca drift.
    /// </summary>
    public partial class AddStockMovementAuditFieldsRev8177 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF to_regclass('"StockMovements"') IS NOT NULL THEN
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'StockMovements' AND column_name = 'OriginType') THEN
                            ALTER TABLE "StockMovements" ADD COLUMN "OriginType" character varying(32) NULL;
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'StockMovements' AND column_name = 'ReasonCode') THEN
                            ALTER TABLE "StockMovements" ADD COLUMN "ReasonCode" character varying(32) NULL;
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'StockMovements' AND column_name = 'AuthorizedByUserId') THEN
                            ALTER TABLE "StockMovements" ADD COLUMN "AuthorizedByUserId" text NULL;
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'StockMovements' AND column_name = 'UnitCostUSD') THEN
                            ALTER TABLE "StockMovements" ADD COLUMN "UnitCostUSD" numeric(18,4) NULL;
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'StockMovements' AND column_name = 'SupplierInvoiceId') THEN
                            ALTER TABLE "StockMovements" ADD COLUMN "SupplierInvoiceId" integer NULL;
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'StockMovements' AND column_name = 'Observation') THEN
                            ALTER TABLE "StockMovements" ADD COLUMN "Observation" text NULL;
                        END IF;

                        CREATE INDEX IF NOT EXISTS "IX_StockMovements_ProductId" ON "StockMovements" ("ProductId");
                        CREATE INDEX IF NOT EXISTS "IX_StockMovements_ReasonCode" ON "StockMovements" ("ReasonCode");
                    END IF;

                    IF to_regclass('"StockMovements_Archive"') IS NOT NULL THEN
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'StockMovements_Archive' AND column_name = 'OriginType') THEN
                            ALTER TABLE "StockMovements_Archive" ADD COLUMN "OriginType" character varying(32) NULL;
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'StockMovements_Archive' AND column_name = 'ReasonCode') THEN
                            ALTER TABLE "StockMovements_Archive" ADD COLUMN "ReasonCode" character varying(32) NULL;
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'StockMovements_Archive' AND column_name = 'AuthorizedByUserId') THEN
                            ALTER TABLE "StockMovements_Archive" ADD COLUMN "AuthorizedByUserId" text NULL;
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'StockMovements_Archive' AND column_name = 'UnitCostUSD') THEN
                            ALTER TABLE "StockMovements_Archive" ADD COLUMN "UnitCostUSD" numeric(18,4) NULL;
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'StockMovements_Archive' AND column_name = 'SupplierInvoiceId') THEN
                            ALTER TABLE "StockMovements_Archive" ADD COLUMN "SupplierInvoiceId" integer NULL;
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'StockMovements_Archive' AND column_name = 'Observation') THEN
                            ALTER TABLE "StockMovements_Archive" ADD COLUMN "Observation" text NULL;
                        END IF;

                        CREATE INDEX IF NOT EXISTS "IX_StockMovements_Archive_ProductId" ON "StockMovements_Archive" ("ProductId");
                        CREATE INDEX IF NOT EXISTS "IX_StockMovements_Archive_ReasonCode" ON "StockMovements_Archive" ("ReasonCode");
                    END IF;

                    RAISE NOTICE '[8.177-T1] Campos de auditoría e índices del Kardex garantizados (idempotente).';
                END $$;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    DROP INDEX IF EXISTS "IX_StockMovements_ReasonCode";
                    DROP INDEX IF EXISTS "IX_StockMovements_Archive_ProductId";
                    DROP INDEX IF EXISTS "IX_StockMovements_Archive_ReasonCode";
                    -- IX_StockMovements_ProductId NO se elimina: preexistía a esta migración (convención FK).

                    IF to_regclass('"StockMovements"') IS NOT NULL THEN
                        IF EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'public' AND table_name = 'StockMovements' AND column_name = 'OriginType') THEN
                            ALTER TABLE "StockMovements" DROP COLUMN "OriginType";
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'public' AND table_name = 'StockMovements' AND column_name = 'ReasonCode') THEN
                            ALTER TABLE "StockMovements" DROP COLUMN "ReasonCode";
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'public' AND table_name = 'StockMovements' AND column_name = 'AuthorizedByUserId') THEN
                            ALTER TABLE "StockMovements" DROP COLUMN "AuthorizedByUserId";
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'public' AND table_name = 'StockMovements' AND column_name = 'UnitCostUSD') THEN
                            ALTER TABLE "StockMovements" DROP COLUMN "UnitCostUSD";
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'public' AND table_name = 'StockMovements' AND column_name = 'SupplierInvoiceId') THEN
                            ALTER TABLE "StockMovements" DROP COLUMN "SupplierInvoiceId";
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'public' AND table_name = 'StockMovements' AND column_name = 'Observation') THEN
                            ALTER TABLE "StockMovements" DROP COLUMN "Observation";
                        END IF;
                    END IF;

                    IF to_regclass('"StockMovements_Archive"') IS NOT NULL THEN
                        IF EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'public' AND table_name = 'StockMovements_Archive' AND column_name = 'OriginType') THEN
                            ALTER TABLE "StockMovements_Archive" DROP COLUMN "OriginType";
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'public' AND table_name = 'StockMovements_Archive' AND column_name = 'ReasonCode') THEN
                            ALTER TABLE "StockMovements_Archive" DROP COLUMN "ReasonCode";
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'public' AND table_name = 'StockMovements_Archive' AND column_name = 'AuthorizedByUserId') THEN
                            ALTER TABLE "StockMovements_Archive" DROP COLUMN "AuthorizedByUserId";
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'public' AND table_name = 'StockMovements_Archive' AND column_name = 'UnitCostUSD') THEN
                            ALTER TABLE "StockMovements_Archive" DROP COLUMN "UnitCostUSD";
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'public' AND table_name = 'StockMovements_Archive' AND column_name = 'SupplierInvoiceId') THEN
                            ALTER TABLE "StockMovements_Archive" DROP COLUMN "SupplierInvoiceId";
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'public' AND table_name = 'StockMovements_Archive' AND column_name = 'Observation') THEN
                            ALTER TABLE "StockMovements_Archive" DROP COLUMN "Observation";
                        END IF;
                    END IF;
                END $$;
                """);
        }
    }
}
