using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddPgTrgm : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS pg_trgm;");
            migrationBuilder.Sql("CREATE INDEX idx_products_name_trgm ON \"Products\" USING gin (\"Name\" gin_trgm_ops);");
            migrationBuilder.Sql("CREATE INDEX idx_products_sku_trgm ON \"Products\" USING gin (\"SKU\" gin_trgm_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX idx_products_name_trgm;");
            migrationBuilder.Sql("DROP INDEX idx_products_sku_trgm;");
            // Note: We typically don't drop extensions in down scripts if other tables might rely on them, 
            // but for completeness of this migration rollback:
            migrationBuilder.Sql("DROP EXTENSION IF EXISTS pg_trgm;");
        }
    }
}
