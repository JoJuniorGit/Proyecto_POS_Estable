using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <summary>
    /// 8.142: elimina la declaracion fantasma de IX_Users_Username. El modelo la declaraba pero
    /// ninguna migracion la creo nunca: la unicidad de Username la garantiza desde 20260823120000
    /// el indice funcional case-insensitive ix_users_username_lower, que EF no puede modelar. Se
    /// emite como SQL crudo idempotente para no fallar en bases que nunca tuvieron el indice
    /// (caso real de CommandCenterDb) y para converger tambien las bases creadas por EnsureCreated.
    /// </summary>
    public partial class DropStaleUsersUsernameIndexRev8142 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""DROP INDEX IF EXISTS "IX_Users_Username";""");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_Users_Username" ON "Users" ("Username");""");
        }
    }
}
