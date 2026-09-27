using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Sales.Module.Data;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <summary>
    /// 8.9-M11: índices GIN pg_trgm sobre las columnas de texto del histórico de ventas que hoy se
    /// buscan con coincidecias parciales (cliente, cédula y cajero). La extensión pg_trgm solo se
    /// crea si el motor la soporta (CREATE EXTENSION requiere permiso); si no está disponible, el
    /// arranque no falla y la búsqueda opera igual (sin aceleración por índice).
    /// 8.12-B3: migración reactivada con [Migration] — antes era huérfana (sin atributo) y los
    /// índices GIN nunca se creaban en ninguna BD. El cuerpo ya es idempotente (guards pg_extension,
    /// to_regclass e information_schema).
    /// </summary>
    [DbContext(typeof(SalesDbContext))]
    [Migration("20260907030000_AddPgTrgmGinIndexesRev89")]
    public partial class AddPgTrgmGinIndexesRev89 : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    has_trgm boolean;
                BEGIN
                    SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'pg_trgm') INTO has_trgm;

                    IF has_trgm THEN
                        IF to_regclass('"Sales"') IS NOT NULL THEN
                            IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Sales' AND column_name = 'CustomerName') THEN
                                CREATE INDEX IF NOT EXISTS "IX_Sales_CustomerName_trgm" ON "Sales" USING gin ("CustomerName" gin_trgm_ops);
                            END IF;
                            IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Sales' AND column_name = 'CustomerCedula') THEN
                                CREATE INDEX IF NOT EXISTS "IX_Sales_CustomerCedula_trgm" ON "Sales" USING gin ("CustomerCedula" gin_trgm_ops);
                            END IF;
                        END IF;

                        IF to_regclass('"Users"') IS NOT NULL THEN
                            IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Users' AND column_name = 'Name') THEN
                                CREATE INDEX IF NOT EXISTS "IX_Users_Name_trgm" ON "Users" USING gin ("Name" gin_trgm_ops);
                            END IF;
                            IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Users' AND column_name = 'FullName') THEN
                                CREATE INDEX IF NOT EXISTS "IX_Users_FullName_trgm" ON "Users" USING gin ("FullName" gin_trgm_ops);
                            END IF;
                            IF EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'Users' AND column_name = 'Cedula') THEN
                                CREATE INDEX IF NOT EXISTS "IX_Users_Cedula_trgm" ON "Users" USING gin ("Cedula" gin_trgm_ops);
                            END IF;
                        END IF;

                        RAISE NOTICE '[8.9-M11] pg_trgm disponible: indices GIN creados para el historico.';
                    ELSE
                        RAISE NOTICE '[8.9-M11] pg_trgm NO disponible: busqueda de historico sin indice GIN (funcional, sin aceleracion).';
                    END IF;
                END $$;
                """);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    DROP INDEX IF EXISTS "IX_Sales_CustomerCedula_trgm";
                    DROP INDEX IF EXISTS "IX_Sales_CustomerName_trgm";
                    DROP INDEX IF EXISTS "IX_Users_Cedula_trgm";
                    DROP INDEX IF EXISTS "IX_Users_FullName_trgm";
                    DROP INDEX IF EXISTS "IX_Users_Name_trgm";
                END $$;
                """);
        }
    }
}