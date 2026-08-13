-- =====================================================================
-- Script SQL de Respaldo Idempotente para Migraciones de Producción
-- =====================================================================

BEGIN;

-- 1. Ampliación de Precisión Decimal (18,4) en Tablas de Ventas
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='SaleItems' AND column_name='Subtotal') THEN
        ALTER TABLE "SaleItems" ALTER COLUMN "Subtotal" TYPE numeric(18,4);
        ALTER TABLE "SaleItems" ALTER COLUMN "SubtotalBsS" TYPE numeric(18,4);
        ALTER TABLE "SaleItems" ALTER COLUMN "UnitPriceBsS" TYPE numeric(18,4);
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='Sales' AND column_name='Subtotal') THEN
        ALTER TABLE "Sales" ALTER COLUMN "Subtotal" TYPE numeric(18,4);
        ALTER TABLE "Sales" ALTER COLUMN "SubtotalBsS" TYPE numeric(18,4);
    END IF;
END $$;

-- 2. Registro en Historia de Migraciones de EF Core
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20260805120000_ExpandSubtotalPrecisionTo4Decimals', '10.0.3')
ON CONFLICT ("MigrationId") DO NOTHING;

COMMIT;
