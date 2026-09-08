using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Inventory.Module.Data;

#nullable disable

namespace Inventory.Module.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(InventoryDbContext))]
    [Migration("20260809145500_RemoveIsServiceFromProducts")]
    public partial class RemoveIsServiceFromProducts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 8.12-B1: migracion reactivada con guard idempotente. En BD legacy la columna
            // IsService existe (creada por 20260313043623_AddServiceProductFlags) y nunca se
            // elimino porque esta migracion era huerfana; en BD fresca la crea esa migracion y
            // esta la elimina -> el modelo final coincide (sin IsService).
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'Products' AND column_name = 'IsService'
    ) THEN
        ALTER TABLE ""Products"" DROP COLUMN ""IsService"";
    END IF;
END $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'Products' AND column_name = 'IsService'
    ) THEN
        ALTER TABLE ""Products"" ADD COLUMN ""IsService"" boolean NOT NULL DEFAULT false;
    END IF;
END $$;");
        }
    }
}