using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Sales.Module.Data;

#nullable disable

namespace Sales.Module.Migrations
{
    [DbContext(typeof(SalesDbContext))]
    [Migration("20260906100000_AddIdempotentRequestsAndCashDrawerSingleOpenIndex")]
    public partial class AddIdempotentRequestsAndCashDrawerSingleOpenIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Tabla formal de IdempotentRequests para persistencia de claves de idempotencia [8B-M5]
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""IdempotentRequests"" (
                    ""Id"" serial PRIMARY KEY,
                    ""Key"" character varying(128) NOT NULL,
                    ""RequestPath"" character varying(256) NOT NULL,
                    ""PayloadHash"" bytea NOT NULL,
                    ""StatusCode"" integer NOT NULL,
                    ""ResponseBody"" text NOT NULL,
                    ""CreatedAtUtc"" timestamp with time zone NOT NULL,
                    ""ExpiresAtUtc"" timestamp with time zone NOT NULL
                );

                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_IdempotentRequests_Key_RequestPath"" 
                    ON ""IdempotentRequests"" (""Key"", ""RequestPath"");

                CREATE INDEX IF NOT EXISTS ""IX_IdempotentRequests_ExpiresAtUtc"" 
                    ON ""IdempotentRequests"" (""ExpiresAtUtc"");

                CREATE INDEX IF NOT EXISTS ""IX_IdempotentRequests_CreatedAtUtc"" 
                    ON ""IdempotentRequests"" (""CreatedAtUtc"");
            ");

            // 2. Índice único parcial para garantizar exclusividad de sesión abierta en caja (Status = 0) [8.2-A2, 8.2-M3]
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_CashDrawerSessions_SingleOpen""
                    ON ""CashDrawerSessions"" (""Status"")
                    WHERE ""Status"" = 0;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""IX_CashDrawerSessions_SingleOpen"";
                DROP TABLE IF EXISTS ""IdempotentRequests"";
            ");
        }
    }
}
