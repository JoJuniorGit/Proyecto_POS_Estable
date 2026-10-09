using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Sales.Module.Data;

#nullable disable

namespace Sales.Module.Migrations
{
    [DbContext(typeof(SalesDbContext))]
    [Migration("20261010120000_PendingDedupeNullSafe")]
    public partial class PendingDedupeNullSafe : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 8.151 (W1, design D1): en PostgreSQL 15+ NULL nunca es igual a NULL, por lo que el
            // indice unico parcial de Pending no dedupe (cajero, NULL, accion). La migracion
            // original queda inmutable: se recrea el indice con NULLS NOT DISTINCT. SQLite
            // conserva la semantica NULL-distinct de EnsureCreated (los bootstraps de tests no
            // aplican migraciones), por eso el guard del proveedor.
            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                return;
            }

            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""IX_AuthorizationRequests_PendingDedupe"";
                CREATE UNIQUE INDEX ""IX_AuthorizationRequests_PendingDedupe""
                    ON ""AuthorizationRequests"" (""RequestedByUserId"", ""SaleId"", ""ActionType"")
                    NULLS NOT DISTINCT
                    WHERE ""Status"" = 0;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider != "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                return;
            }

            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""IX_AuthorizationRequests_PendingDedupe"";
                CREATE UNIQUE INDEX ""IX_AuthorizationRequests_PendingDedupe""
                    ON ""AuthorizationRequests"" (""RequestedByUserId"", ""SaleId"", ""ActionType"")
                    WHERE ""Status"" = 0;
            ");
        }
    }
}
