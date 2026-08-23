using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddCaseInsensitiveUsernameIndexAndBackfill : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Backfill legacy users with empty/null Username defensibly
            migrationBuilder.Sql(@"
                UPDATE ""Users""
                SET ""Username"" = COALESCE(NULLIF(TRIM(""Username""), ''), NULLIF(TRIM(""Cedula""), ''), 'user_' || ""Id""::text)
                WHERE ""Username"" IS NULL OR TRIM(""Username"") = '';
            ");

            // 2. Functional unique index for case-insensitive uniqueness in PostgreSQL
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX IF NOT EXISTS ""ix_users_username_lower"" ON ""Users"" (LOWER(""Username""));
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""ix_users_username_lower"";
            ");
        }
    }
}
