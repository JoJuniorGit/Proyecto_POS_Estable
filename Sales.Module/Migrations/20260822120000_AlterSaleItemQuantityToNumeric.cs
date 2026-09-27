using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Sales.Module.Data;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(SalesDbContext))]
    [Migration("20260822120000_AlterSaleItemQuantityToNumeric")]
    public partial class AlterSaleItemQuantityToNumeric : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 8.12-B1: reactivada con guard completo (data_type + precision + scale), mismo
            // criterio que el bloque de convergencia (M10). No-op si ya es numeric(18,3).
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'SaleItems' AND column_name = 'Quantity'
          AND (data_type <> 'numeric' OR numeric_precision <> 18 OR numeric_scale <> 3)
    ) THEN
        ALTER TABLE ""SaleItems"" ALTER COLUMN ""Quantity"" TYPE numeric(18,3);
        RAISE NOTICE 'Column SaleItems.Quantity altered to numeric(18,3)';
    END IF;
END $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'SaleItems' AND column_name = 'Quantity'
          AND (data_type <> 'numeric' OR numeric_precision <> 18 OR numeric_scale <> 3)
    ) THEN
        ALTER TABLE ""SaleItems"" ALTER COLUMN ""Quantity"" TYPE numeric(18,3);
    END IF;
END $$;");
        }
    }
}