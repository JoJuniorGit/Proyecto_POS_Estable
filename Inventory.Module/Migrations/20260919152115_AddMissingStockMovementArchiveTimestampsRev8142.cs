using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Module.Migrations
{
    /// <summary>
    /// SEAM-03: las columnas CreatedAt y UpdatedAt de StockMovementArchive (heredadas de BaseEntity)
    /// las declara el modelo pero la migracion original del archivo no las creo (drift expuesto por
    /// MigrationIndexDriftTests). El DDL es idempotente porque una base creada con EnsureCreated ya
    /// puede tenerlas.
    /// </summary>
    public partial class AddMissingStockMovementArchiveTimestampsRev8142 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE ""StockMovements_Archive"" ADD COLUMN IF NOT EXISTS ""CreatedAt"" timestamp with time zone NOT NULL DEFAULT now();
                ALTER TABLE ""StockMovements_Archive"" ALTER COLUMN ""CreatedAt"" DROP DEFAULT;
                ALTER TABLE ""StockMovements_Archive"" ADD COLUMN IF NOT EXISTS ""UpdatedAt"" timestamp with time zone NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE ""StockMovements_Archive"" DROP COLUMN IF EXISTS ""UpdatedAt"";
                ALTER TABLE ""StockMovements_Archive"" DROP COLUMN IF EXISTS ""CreatedAt"";
            ");
        }
    }
}
