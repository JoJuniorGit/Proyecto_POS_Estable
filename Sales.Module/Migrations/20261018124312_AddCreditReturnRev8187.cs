using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <summary>
    /// 8.187-T3b2 (Paso 11/12, S5/S7): devoluciones de ventas a crédito y reembolso del overpago.
    /// Aditiva sobre bases pobladas: las tablas nuevas (`CreditReturns`, `CreditReturnItems`) usan
    /// `CREATE TABLE/INDEX IF NOT EXISTS` (estilo 8.150/F1) con FKs RESTRICT, índice único
    /// filtrado de idempotencia y guard `DO $$` idempotente. Sin cambios a tablas existentes.
    /// Down es espejo y seguro (hijos primero).
    /// </summary>
    public partial class AddCreditReturnRev8187 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "CreditReturns" (
                    "Id" serial PRIMARY KEY,
                    "SaleId" integer NOT NULL,
                    "ReceivableId" integer NOT NULL,
                    "CustomerId" integer NOT NULL,
                    "ReturnedUSD" numeric(18,4) NOT NULL,
                    "RefundedUSD" numeric(18,4) NOT NULL,
                    "RefundedBsS" numeric(18,2) NOT NULL,
                    "RefundRate" numeric(18,4) NULL,
                    "CashTransactionId" integer NULL,
                    "CreatedAtUtc" timestamp with time zone NOT NULL,
                    "Reason" character varying(500) NULL,
                    "CreatedByUserId" integer NULL,
                    "IdempotencyKey" character varying(100) NULL,
                    CONSTRAINT "FK_CreditReturns_CashTransactions_CashTransactionId" FOREIGN KEY ("CashTransactionId") REFERENCES "CashTransactions" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_CreditReturns_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES "Customers" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_CreditReturns_Receivables_ReceivableId" FOREIGN KEY ("ReceivableId") REFERENCES "Receivables" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_CreditReturns_Sales_SaleId" FOREIGN KEY ("SaleId") REFERENCES "Sales" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_CreditReturns_Users_CreatedByUserId" FOREIGN KEY ("CreatedByUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
                );

                CREATE TABLE IF NOT EXISTS "CreditReturnItems" (
                    "Id" serial PRIMARY KEY,
                    "CreditReturnId" integer NOT NULL,
                    "SaleItemId" integer NOT NULL,
                    "Quantity" numeric(18,3) NOT NULL,
                    "AmountUSD" numeric(18,4) NOT NULL,
                    CONSTRAINT "FK_CreditReturnItems_CreditReturns_CreditReturnId" FOREIGN KEY ("CreditReturnId") REFERENCES "CreditReturns" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_CreditReturnItems_SaleItems_SaleItemId" FOREIGN KEY ("SaleItemId") REFERENCES "SaleItems" ("Id") ON DELETE RESTRICT
                );

                CREATE INDEX IF NOT EXISTS "IX_CreditReturnItems_CreditReturnId"
                    ON "CreditReturnItems" ("CreditReturnId");

                CREATE INDEX IF NOT EXISTS "IX_CreditReturnItems_SaleItemId"
                    ON "CreditReturnItems" ("SaleItemId");

                CREATE INDEX IF NOT EXISTS "IX_CreditReturns_CashTransactionId"
                    ON "CreditReturns" ("CashTransactionId");

                CREATE INDEX IF NOT EXISTS "IX_CreditReturns_CreatedByUserId"
                    ON "CreditReturns" ("CreatedByUserId");

                CREATE INDEX IF NOT EXISTS "IX_CreditReturns_CustomerId"
                    ON "CreditReturns" ("CustomerId");

                CREATE UNIQUE INDEX IF NOT EXISTS "IX_CreditReturns_IdempotencyKey"
                    ON "CreditReturns" ("IdempotencyKey")
                    WHERE "IdempotencyKey" IS NOT NULL;

                CREATE INDEX IF NOT EXISTS "IX_CreditReturns_ReceivableId"
                    ON "CreditReturns" ("ReceivableId");

                CREATE INDEX IF NOT EXISTS "IX_CreditReturns_SaleId"
                    ON "CreditReturns" ("SaleId");

                DO $$
                BEGIN
                    RAISE NOTICE '[8.187-T3b2] Devoluciones CxC garantizadas (idempotente).';
                END $$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS "CreditReturnItems";
                DROP TABLE IF EXISTS "CreditReturns";
                """);
        }
    }
}
