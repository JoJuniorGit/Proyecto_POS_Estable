using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Module.Migrations
{
    /// <inheritdoc />
    public partial class AlterInventoryQuantitiesToNumeric : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
BEGIN
    -- Parent Table: Products
    IF EXISTS (
        SELECT 1 FROM information_schema.columns 
        WHERE table_schema = 'public' AND table_name = 'Products' AND column_name = 'StockQuantity' AND data_type <> 'numeric'
    ) THEN
        ALTER TABLE ""Products"" ALTER COLUMN ""StockQuantity"" TYPE numeric(18,3);
    END IF;

    IF EXISTS (
        SELECT 1 FROM information_schema.columns 
        WHERE table_schema = 'public' AND table_name = 'Products' AND column_name = 'ReservedQuantity' AND data_type <> 'numeric'
    ) THEN
        ALTER TABLE ""Products"" ALTER COLUMN ""ReservedQuantity"" TYPE numeric(18,3);
    END IF;

    IF EXISTS (
        SELECT 1 FROM information_schema.columns 
        WHERE table_schema = 'public' AND table_name = 'Products' AND column_name = 'LowStockThreshold' AND data_type <> 'numeric'
    ) THEN
        ALTER TABLE ""Products"" ALTER COLUMN ""LowStockThreshold"" TYPE numeric(18,3);
    END IF;

    IF EXISTS (
        SELECT 1 FROM information_schema.columns 
        WHERE table_schema = 'public' AND table_name = 'Products' AND column_name = 'MinWholesaleQuantity' AND data_type <> 'numeric'
    ) THEN
        ALTER TABLE ""Products"" ALTER COLUMN ""MinWholesaleQuantity"" TYPE numeric(18,3);
    END IF;

    -- Child Tables: StockMovements, StockReservations
    IF EXISTS (
        SELECT 1 FROM information_schema.columns 
        WHERE table_schema = 'public' AND table_name = 'StockMovements' AND column_name = 'QuantityChange' AND data_type <> 'numeric'
    ) THEN
        ALTER TABLE ""StockMovements"" ALTER COLUMN ""QuantityChange"" TYPE numeric(18,3);
    END IF;

    IF EXISTS (
        SELECT 1 FROM information_schema.columns 
        WHERE table_schema = 'public' AND table_name = 'StockMovements' AND column_name = 'NewStockLevel' AND data_type <> 'numeric'
    ) THEN
        ALTER TABLE ""StockMovements"" ALTER COLUMN ""NewStockLevel"" TYPE numeric(18,3);
    END IF;

    IF EXISTS (
        SELECT 1 FROM information_schema.columns 
        WHERE table_schema = 'public' AND table_name = 'StockReservations' AND column_name = 'Quantity' AND data_type <> 'numeric'
    ) THEN
        ALTER TABLE ""StockReservations"" ALTER COLUMN ""Quantity"" TYPE numeric(18,3);
    END IF;
END $$;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
