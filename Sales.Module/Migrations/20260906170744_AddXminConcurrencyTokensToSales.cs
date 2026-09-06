using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <summary>
    /// Migración de sincronización del modelo (operación mínima e idempotente).
    ///
    /// Registra en el snapshot del modelo los tokens de concurrencia `xmin` de
    /// PostgreSQL para `Sale` y `CashDrawerSession` (hallazgo 8.2-A1) y sana la
    /// desincronización previa del snapshot (faltaban `IdempotentRequests` y
    /// `OutboxMessages`).
    ///
    /// Única operación de esquema real: crea `OutboxMessages` de forma idempotente
    /// (`CREATE TABLE IF NOT EXISTS`), tabla que hasta ahora solo garantizaba el arranque
    /// desde SQL crudo en `Program.cs` (8B-M5) y que así queda cubierta por migraciones.
    /// `xmin` es una pseudo-columna de sistema que PostgreSQL expone automáticamente en
    /// cada fila, por lo que NO requiere DDL de adición. Los objetos existentes no se
    /// recrean ni destruyen, garantizando seguridad en bases de datos ya desplegadas.
    /// </summary>
    public partial class AddXminConcurrencyTokensToSales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE TABLE IF NOT EXISTS ""OutboxMessages"" (
                    ""Id"" uuid NOT NULL PRIMARY KEY,
                    ""EventType"" character varying(100) NOT NULL,
                    ""Payload"" jsonb NOT NULL,
                    ""CreatedAtUtc"" timestamp with time zone NOT NULL,
                    ""ProcessedAtUtc"" timestamp with time zone NULL,
                    ""DispatchedAtUtc"" timestamp with time zone NULL,
                    ""Status"" character varying(20) NOT NULL DEFAULT 'Pending',
                    ""RetryCount"" integer NOT NULL DEFAULT 0,
                    ""NextRetryUtc"" timestamp with time zone NOT NULL,
                    ""ErrorMessage"" text NULL
                );

                CREATE INDEX IF NOT EXISTS ""IX_OutboxMessages_CreatedAtUtc""
                    ON ""OutboxMessages"" (""CreatedAtUtc"");

                CREATE INDEX IF NOT EXISTS ""IX_OutboxMessages_Status_NextRetryUtc""
                    ON ""OutboxMessages"" (""Status"", ""NextRetryUtc"")
                    WHERE ""Status"" = 'Pending';
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP TABLE IF EXISTS ""OutboxMessages"";
            ");
        }
    }
}