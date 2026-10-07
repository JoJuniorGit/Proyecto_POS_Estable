using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Sales.Module.Data;

#nullable disable

namespace Sales.Module.Migrations
{
    [DbContext(typeof(SalesDbContext))]
    [Migration("20261007120000_AddRemoteAuthorizationHubDataLayer")]
    public partial class AddRemoteAuthorizationHubDataLayer : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 8.150 (T1): capa de datos del hub de autorizaciones remotas (design D1).
            // Sin FK ni navegaciones: la auditoria conserva snapshots de nombres.
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""AuthorizationRequests"" (
                    ""Id"" serial PRIMARY KEY,
                    ""ActionType"" integer NOT NULL,
                    ""SaleId"" integer NULL,
                    ""RequestedByUserId"" integer NOT NULL,
                    ""RequestedByName"" text NOT NULL,
                    ""Terminal"" text NULL,
                    ""Status"" integer NOT NULL,
                    ""ResolutionMode"" integer NULL,
                    ""ResolvedByUserId"" integer NULL,
                    ""ResolvedByName"" text NULL,
                    ""ResolutionReason"" text NULL,
                    ""ContextJson"" text NOT NULL,
                    ""ContextHash"" character varying(64) NOT NULL,
                    ""CreatedAt"" timestamp with time zone NOT NULL,
                    ""ExpiresAt"" timestamp with time zone NOT NULL,
                    ""ResolvedAt"" timestamp with time zone NULL,
                    ""ConsumedAt"" timestamp with time zone NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_AuthorizationRequests_PendingDedupe""
                    ON ""AuthorizationRequests"" (""RequestedByUserId"", ""SaleId"", ""ActionType"")
                    WHERE ""Status"" = 0;

                CREATE INDEX IF NOT EXISTS ""IX_AuthorizationRequests_Status_ExpiresAt""
                    ON ""AuthorizationRequests"" (""Status"", ""ExpiresAt"");

                CREATE TABLE IF NOT EXISTS ""AuthorizationAudits"" (
                    ""Id"" serial PRIMARY KEY,
                    ""RequestId"" integer NOT NULL,
                    ""ActionType"" integer NOT NULL,
                    ""SaleId"" integer NULL,
                    ""Terminal"" text NULL,
                    ""RequestedByUserId"" integer NOT NULL,
                    ""RequestedByName"" text NOT NULL,
                    ""Status"" integer NOT NULL,
                    ""ResolutionMode"" integer NULL,
                    ""ResolvedByUserId"" integer NULL,
                    ""ResolvedByName"" text NULL,
                    ""Reason"" text NULL,
                    ""ContextJson"" text NULL,
                    ""RequestedAt"" timestamp with time zone NOT NULL,
                    ""ResolvedAt"" timestamp with time zone NOT NULL
                );

                CREATE INDEX IF NOT EXISTS ""IX_AuthorizationAudits_RequestId""
                    ON ""AuthorizationAudits"" (""RequestId"");
            ");

            // Trigger append-only solo en PostgreSQL: los bootstraps de tests (EnsureCreated)
            // no aplican migraciones y no necesitan el guard.
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql(@"
                    CREATE OR REPLACE FUNCTION prevent_authorization_audit_mutation()
                    RETURNS trigger AS $$
                    BEGIN
                        RAISE EXCEPTION 'AuthorizationAudits es append-only: % no permitido', TG_OP;
                    END;
                    $$ LANGUAGE plpgsql;

                    DROP TRIGGER IF EXISTS trg_authorization_audits_append_only ON ""AuthorizationAudits"";
                    CREATE TRIGGER trg_authorization_audits_append_only
                        BEFORE UPDATE OR DELETE ON ""AuthorizationAudits""
                        FOR EACH ROW
                        EXECUTE FUNCTION prevent_authorization_audit_mutation();
                ");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            if (migrationBuilder.ActiveProvider == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                migrationBuilder.Sql(@"
                    DROP TRIGGER IF EXISTS trg_authorization_audits_append_only ON ""AuthorizationAudits"";
                    DROP FUNCTION IF EXISTS prevent_authorization_audit_mutation();
                ");
            }

            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""IX_AuthorizationRequests_PendingDedupe"";
                DROP INDEX IF EXISTS ""IX_AuthorizationRequests_Status_ExpiresAt"";
                DROP INDEX IF EXISTS ""IX_AuthorizationAudits_RequestId"";
                DROP TABLE IF EXISTS ""AuthorizationAudits"";
                DROP TABLE IF EXISTS ""AuthorizationRequests"";
            ");
        }
    }
}
