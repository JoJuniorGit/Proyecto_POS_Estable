using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Module.Migrations
{
    /// <summary>
    /// 8.16-H03: clave estructurada de idempotencia de la deducción (SaleId), independiente
    /// del string Reason. Nullable: movimientos no derivados de venta o históricos.
    /// 8.20-C01: migración reactivada con [Migration] — antes era huérfana (sin atributo ni
    /// .Designer.cs) y EF Core la omitía en discovery, por lo que MigrateAsync nunca la
    /// aplicaba y SaleId solo existía en BDs creadas con EnsureCreated. El cuerpo ya es
    /// idempotente (guards to_regclass e information_schema): si la columna/índice ya existen,
    /// la aplicación es un no-op y no provoca drift.
    /// </summary>
    [DbContext(typeof(InventoryDbContext))]
    [Migration("20260908153000_AddSaleIdToStockMovement")]
    public partial class AddSaleIdToStockMovement : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF to_regclass('"StockMovements"') IS NOT NULL THEN
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'StockMovements' AND column_name = 'SaleId') THEN
                            ALTER TABLE "StockMovements" ADD COLUMN "SaleId" integer NULL;
                        END IF;
                        CREATE INDEX IF NOT EXISTS "IX_StockMovements_SaleId" ON "StockMovements" ("SaleId");
                    END IF;

                    IF to_regclass('"StockMovements_Archive"') IS NOT NULL THEN
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'StockMovements_Archive' AND column_name = 'SaleId') THEN
                            ALTER TABLE "StockMovements_Archive" ADD COLUMN "SaleId" integer NULL;
                        END IF;
                    END IF;

                    RAISE NOTICE '[8.20-C01] SaleId e IX_StockMovements_SaleId garantizados (idempotente).';
                END $$;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    DROP INDEX IF EXISTS "IX_StockMovements_SaleId";

                    IF to_regclass('"StockMovements_Archive"') IS NOT NULL THEN
                        IF EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'public' AND table_name = 'StockMovements_Archive' AND column_name = 'SaleId') THEN
                            ALTER TABLE "StockMovements_Archive" DROP COLUMN "SaleId";
                        END IF;
                    END IF;

                    IF to_regclass('"StockMovements"') IS NOT NULL THEN
                        IF EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'public' AND table_name = 'StockMovements' AND column_name = 'SaleId') THEN
                            ALTER TABLE "StockMovements" DROP COLUMN "SaleId";
                        END IF;
                    END IF;
                END $$;
                """);
        }
    }
}