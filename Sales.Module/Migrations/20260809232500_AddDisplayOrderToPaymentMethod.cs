using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Sales.Module.Data;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(SalesDbContext))]
    [Migration("20260809232500_AddDisplayOrderToPaymentMethod")]
    public partial class AddDisplayOrderToPaymentMethod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 8.12-B1/B3: migracion reactivada con guard information_schema idempotente.
            // En BD legacy la columna ya existe (bloque de convergencia o autocuracion 8.11-F),
            // por lo que el ADD COLUMN directo fallaria; el guard lo convierte en no-op.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'PaymentMethods' AND column_name = 'DisplayOrder'
    ) THEN
        ALTER TABLE ""PaymentMethods"" ADD COLUMN ""DisplayOrder"" integer NOT NULL DEFAULT 0;
    END IF;
END $$;");

            // 8.12-M3: sembrar DisplayOrder=1/2 para los metodos sembrados por HasData
            // ("Cash" Id=1, "Card" Id=2) SOLO si siguen en 0 (no pisa reordenamientos
            // manuales posteriores). El seed de produccion no usa nombres "Efectivo USD".
            migrationBuilder.Sql(@"
                UPDATE ""PaymentMethods"" SET ""DisplayOrder"" = 1 WHERE ""Name"" = 'Cash' AND ""DisplayOrder"" = 0;
                UPDATE ""PaymentMethods"" SET ""DisplayOrder"" = 2 WHERE ""Name"" = 'Card' AND ""DisplayOrder"" = 0;
            ");

            // 8.14-N2: fallback por Id — si Cash/Card fueron RENOMBRADOS en una BD legacy
            // (p. ej. "Efectivo USD") el UPDATE por nombre no aplica y quedarian en 0
            // (OrdenBy los pondria primeros). Se asigna un orden secuencial por Id solo a
            // los que siguen en 0, respetando cualquier DisplayOrder ya asignado.
            migrationBuilder.Sql(@"
                WITH ranked AS (
                    SELECT ""Id"", ROW_NUMBER() OVER (ORDER BY ""Id"") AS rn
                    FROM ""PaymentMethods""
                    WHERE ""DisplayOrder"" = 0
                )
                UPDATE ""PaymentMethods"" p
                SET ""DisplayOrder"" = r.rn
                FROM ranked r
                WHERE p.""Id"" = r.""Id"";
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'PaymentMethods' AND column_name = 'DisplayOrder'
    ) THEN
        ALTER TABLE ""PaymentMethods"" DROP COLUMN ""DisplayOrder"";
    END IF;
END $$;");
        }
    }
}