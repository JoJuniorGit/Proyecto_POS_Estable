using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <summary>
    /// SEAM-03: columnas que el modelo declara y ninguna migracion creaba (drift expuesto por
    /// MigrationIndexDriftTests). En bases creadas con EnsureCreated ya existen, por eso el DDL es
    /// idempotente (`IF NOT EXISTS`); una base creada solo con MigrateAsync no podia autenticar.
    /// </summary>
    public partial class AddMissingUserSecurityColumnsRev8142 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""SecurityStamp"" text NOT NULL DEFAULT '';
                ALTER TABLE ""Users"" ALTER COLUMN ""SecurityStamp"" DROP DEFAULT;
                ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""AccessFailedCount"" integer NOT NULL DEFAULT 0;
                ALTER TABLE ""Users"" ALTER COLUMN ""AccessFailedCount"" DROP DEFAULT;
                ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""LastLoginUtc"" timestamp with time zone NULL;
                ALTER TABLE ""Users"" ADD COLUMN IF NOT EXISTS ""LockoutEndUtc"" timestamp with time zone NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                ALTER TABLE ""Users"" DROP COLUMN IF EXISTS ""LockoutEndUtc"";
                ALTER TABLE ""Users"" DROP COLUMN IF EXISTS ""LastLoginUtc"";
                ALTER TABLE ""Users"" DROP COLUMN IF EXISTS ""AccessFailedCount"";
                ALTER TABLE ""Users"" DROP COLUMN IF EXISTS ""SecurityStamp"";
            ");
        }
    }
}
