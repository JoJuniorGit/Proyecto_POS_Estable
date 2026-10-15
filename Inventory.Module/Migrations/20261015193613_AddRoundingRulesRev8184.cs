using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Module.Migrations
{
    /// <summary>
    /// 8.184-T2 (S5): reglas de redondeo por artículo (Products.RoundingRuleUsd,
    /// Products.RoundingRuleCashBs) y por línea de factura proveedor (SupplierInvoiceLines.RoundingRule),
    /// todas nullable (null = hereda la política global / la regla de la factura). Los enums se
    /// persisten por NOMBRE (character varying(32), estilo 8.177/StockMovement).
    /// El cuerpo usa guards to_regclass/information_schema, estilo 8.177
    /// (AddStockMovementAuditFieldsRev8177): si la estructura ya existe la aplicación es un no-op
    /// y no provoca drift. Down es espejo (DROP COLUMN guardado).
    /// </summary>
    public partial class AddRoundingRulesRev8184 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF to_regclass('"Products"') IS NOT NULL THEN
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'Products' AND column_name = 'RoundingRuleUsd') THEN
                            ALTER TABLE "Products" ADD COLUMN "RoundingRuleUsd" character varying(32) NULL;
                        END IF;
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'Products' AND column_name = 'RoundingRuleCashBs') THEN
                            ALTER TABLE "Products" ADD COLUMN "RoundingRuleCashBs" character varying(32) NULL;
                        END IF;
                    END IF;

                    IF to_regclass('"SupplierInvoiceLines"') IS NOT NULL THEN
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'SupplierInvoiceLines' AND column_name = 'RoundingRule') THEN
                            ALTER TABLE "SupplierInvoiceLines" ADD COLUMN "RoundingRule" character varying(32) NULL;
                        END IF;
                    END IF;

                    RAISE NOTICE '[8.184-T2] Reglas de redondeo por artículo y por línea garantizadas (idempotente).';
                END $$;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF to_regclass('"Products"') IS NOT NULL THEN
                        IF EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'public' AND table_name = 'Products' AND column_name = 'RoundingRuleUsd') THEN
                            ALTER TABLE "Products" DROP COLUMN "RoundingRuleUsd";
                        END IF;
                        IF EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'public' AND table_name = 'Products' AND column_name = 'RoundingRuleCashBs') THEN
                            ALTER TABLE "Products" DROP COLUMN "RoundingRuleCashBs";
                        END IF;
                    END IF;

                    IF to_regclass('"SupplierInvoiceLines"') IS NOT NULL THEN
                        IF EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'public' AND table_name = 'SupplierInvoiceLines' AND column_name = 'RoundingRule') THEN
                            ALTER TABLE "SupplierInvoiceLines" DROP COLUMN "RoundingRule";
                        END IF;
                    END IF;
                END $$;
                """);
        }
    }
}
