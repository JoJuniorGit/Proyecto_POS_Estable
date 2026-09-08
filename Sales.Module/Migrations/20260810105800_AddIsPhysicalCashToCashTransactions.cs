using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Sales.Module.Data;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(SalesDbContext))]
    [Migration("20260810105800_AddIsPhysicalCashToCashTransactions")]
    public partial class AddIsPhysicalCashToCashTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 8.12-B1: migracion reactivada con guard idempotente. La version original declaraba
            // type "INTEGER" para una columna booleana (incompatible con el modelo bool de EF);
            // el guard crea la columna como boolean si falta (mismo shape que el bloque de
            // convergencia de Program.cs) y es no-op en BD legacy donde ya existe.
            migrationBuilder.Sql(@"
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'CashTransactions' AND column_name = 'IsPhysicalCash'
    ) THEN
        ALTER TABLE ""CashTransactions"" ADD COLUMN ""IsPhysicalCash"" boolean NOT NULL DEFAULT true;
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
        WHERE table_schema = 'public' AND table_name = 'CashTransactions' AND column_name = 'IsPhysicalCash'
    ) THEN
        ALTER TABLE ""CashTransactions"" DROP COLUMN ""IsPhysicalCash"";
    END IF;
END $$;");
        }
    }
}