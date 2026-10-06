using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Sales.Module.Data;

#nullable disable

namespace Sales.Module.Migrations
{
    [DbContext(typeof(SalesDbContext))]
    [Migration("20261005120000_AddUniqueClosureDateIndex")]
    public partial class AddUniqueClosureDateIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 8.149 (SRE-02): un único cierre por fecha. Se recrea el índice canónico
            // IX_DailyClosures_ClosureDate como UNIQUE para alinear la BD con el modelo
            // (SalesDbContext: HasIndex(ClosureDate).IsUnique()).
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""IX_DailyClosures_ClosureDate"";
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_DailyClosures_ClosureDate""
                    ON ""DailyClosures"" (""ClosureDate"");
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""IX_DailyClosures_ClosureDate"";
                CREATE INDEX IF NOT EXISTS ""IX_DailyClosures_ClosureDate""
                    ON ""DailyClosures"" (""ClosureDate"");
            ");
        }
    }
}
