using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Sales.Module.Data;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(SalesDbContext))]
    [Migration("20260805120000_ExpandSubtotalPrecisionTo4Decimals")]
    public partial class ExpandSubtotalPrecisionTo4Decimals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 8.12-B2: esta migracion quedo HUERFANA (sin atributo [Migration]) y EF Core la
            // omitia: en instalaciones frescas Subtotal/UnitPrice quedaban numeric(18,2) mientras
            // el modelo declara decimal(18,4) -> redondeo silencioso de centesimas en persistencia.
            // Activada con ALTER idempotente (no-op si la columna ya es numeric(18,4)).
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'SaleItems' AND column_name = 'UnitPriceBsS') THEN
        ALTER TABLE ""SaleItems"" ALTER COLUMN ""UnitPriceBsS"" TYPE numeric(18,4);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'SaleItems' AND column_name = 'UnitPrice') THEN
        ALTER TABLE ""SaleItems"" ALTER COLUMN ""UnitPrice"" TYPE numeric(18,4);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'SaleItems' AND column_name = 'SubtotalBsS') THEN
        ALTER TABLE ""SaleItems"" ALTER COLUMN ""SubtotalBsS"" TYPE numeric(18,4);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'SaleItems' AND column_name = 'Subtotal') THEN
        ALTER TABLE ""SaleItems"" ALTER COLUMN ""Subtotal"" TYPE numeric(18,4);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Sales' AND column_name = 'SubtotalBsS') THEN
        ALTER TABLE ""Sales"" ALTER COLUMN ""SubtotalBsS"" TYPE numeric(18,4);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Sales' AND column_name = 'Subtotal') THEN
        ALTER TABLE ""Sales"" ALTER COLUMN ""Subtotal"" TYPE numeric(18,4);
    END IF;
END $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'SaleItems' AND column_name = 'UnitPriceBsS') THEN
        ALTER TABLE ""SaleItems"" ALTER COLUMN ""UnitPriceBsS"" TYPE numeric(18,2);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'SaleItems' AND column_name = 'UnitPrice') THEN
        ALTER TABLE ""SaleItems"" ALTER COLUMN ""UnitPrice"" TYPE numeric(18,2);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'SaleItems' AND column_name = 'SubtotalBsS') THEN
        ALTER TABLE ""SaleItems"" ALTER COLUMN ""SubtotalBsS"" TYPE numeric(18,2);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'SaleItems' AND column_name = 'Subtotal') THEN
        ALTER TABLE ""SaleItems"" ALTER COLUMN ""Subtotal"" TYPE numeric(18,2);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Sales' AND column_name = 'SubtotalBsS') THEN
        ALTER TABLE ""Sales"" ALTER COLUMN ""SubtotalBsS"" TYPE numeric(18,2);
    END IF;
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'Sales' AND column_name = 'Subtotal') THEN
        ALTER TABLE ""Sales"" ALTER COLUMN ""Subtotal"" TYPE numeric(18,2);
    END IF;
END $$;");
        }
    }
}