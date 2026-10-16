using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <summary>
    /// 8.185-T2 (S5): regla de efectivo resuelta por línea de venta
    /// (SaleItems.RoundingRuleCashBs), nullable (null = línea previa a 8.185 sin política).
    /// El enum se persiste por NOMBRE (character varying(32), estilo 8.184 Product).
    /// El cuerpo usa guards to_regclass/information_schema, estilo 8.177/8.184
    /// (AddRoundingRulesRev8184): si la estructura ya existe la aplicación es un no-op
    /// y no provoca drift. Down es espejo (DROP COLUMN guardado).
    /// </summary>
    public partial class AddSaleItemRoundingRuleRev8185 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF to_regclass('"SaleItems"') IS NOT NULL THEN
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'SaleItems' AND column_name = 'RoundingRuleCashBs') THEN
                            ALTER TABLE "SaleItems" ADD COLUMN "RoundingRuleCashBs" character varying(32) NULL;
                        END IF;
                    END IF;

                    RAISE NOTICE '[8.185-T2] Regla de efectivo por línea garantizada (idempotente).';
                END $$;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF to_regclass('"SaleItems"') IS NOT NULL THEN
                        IF EXISTS (SELECT 1 FROM information_schema.columns
                                   WHERE table_schema = 'public' AND table_name = 'SaleItems' AND column_name = 'RoundingRuleCashBs') THEN
                            ALTER TABLE "SaleItems" DROP COLUMN "RoundingRuleCashBs";
                        END IF;
                    END IF;
                END $$;
                """);
        }
    }
}
