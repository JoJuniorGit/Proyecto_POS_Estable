using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Module.Migrations
{
    /// <summary>
    /// WU1: alcance por producto del conteo físico. Migración aditiva e idempotente: la columna
    /// ScopeType nace con default All y las sesiones históricas con categoría se backfillean a
    /// Category. La tabla puente usa clave compuesta y FKs Restrict. No modifica migraciones previas.
    /// </summary>
    public partial class AddStockCountProductScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                ALTER TABLE "StockCountSessions"
                    ADD COLUMN IF NOT EXISTS "ScopeType" character varying(32) NOT NULL DEFAULT 'All';

                UPDATE "StockCountSessions"
                SET "ScopeType" = 'Category'
                WHERE "ScopeCategoryId" IS NOT NULL;

                CREATE TABLE IF NOT EXISTS "StockCountSessionProducts" (
                    "StockCountSessionId" integer NOT NULL,
                    "ProductId" integer NOT NULL,
                    CONSTRAINT "PK_StockCountSessionProducts" PRIMARY KEY ("StockCountSessionId", "ProductId"),
                    CONSTRAINT "FK_StockCountSessionProducts_Products_ProductId" FOREIGN KEY ("ProductId") REFERENCES "Products" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_StockCountSessionProducts_StockCountSessions_StockCountSessionId" FOREIGN KEY ("StockCountSessionId") REFERENCES "StockCountSessions" ("Id") ON DELETE RESTRICT
                );

                CREATE INDEX IF NOT EXISTS "IX_StockCountSessionProducts_ProductId"
                    ON "StockCountSessionProducts" ("ProductId");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS "StockCountSessionProducts";
                ALTER TABLE "StockCountSessions" DROP COLUMN IF EXISTS "ScopeType";
                """);
        }
    }
}
