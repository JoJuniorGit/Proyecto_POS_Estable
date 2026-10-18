using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <summary>
    /// 8.187 F1 (Paso 11 + Paso 12): dominio y persistencia del credito CxC bimonetario.
    /// Aditiva sobre bases pobladas: las columnas de <c>Customers</c> se agregan con guards
    /// `information_schema.columns` y defaults seguros (credito deshabilitado), y las tablas
    /// nuevas (`Receivables`, `ReceivablePayments`, `CreditPaymentAllocations`) usan
    /// `CREATE TABLE/INDEX IF NOT EXISTS` mas indices con filtro, estilo 8.150.
    /// `xmin` es pseudo-columna de sistema: no requiere DDL. Down es espejo y seguro.
    /// </summary>
    public partial class AddCreditReceivableCxcRev8187 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF to_regclass('"Customers"') IS NOT NULL THEN
                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'Customers' AND column_name = 'IsCreditEnabled') THEN
                            ALTER TABLE "Customers" ADD COLUMN "IsCreditEnabled" boolean NOT NULL DEFAULT false;
                        END IF;

                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'Customers' AND column_name = 'PaymentTermDays') THEN
                            ALTER TABLE "Customers" ADD COLUMN "PaymentTermDays" integer NOT NULL DEFAULT 30;
                        END IF;

                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'Customers' AND column_name = 'CreditGraceDays') THEN
                            ALTER TABLE "Customers" ADD COLUMN "CreditGraceDays" integer NOT NULL DEFAULT 0;
                        END IF;

                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'Customers' AND column_name = 'CreditStatus') THEN
                            ALTER TABLE "Customers" ADD COLUMN "CreditStatus" integer NOT NULL DEFAULT 0;
                        END IF;

                        IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                                       WHERE table_schema = 'public' AND table_name = 'Customers' AND column_name = 'CreditNotes') THEN
                            ALTER TABLE "Customers" ADD COLUMN "CreditNotes" character varying(1000) NULL;
                        END IF;
                    END IF;

                    RAISE NOTICE '[8.187-F1] Perfil crediticio de Customers garantizado (idempotente).';
                END $$;
                """);

            migrationBuilder.Sql(
                """
                CREATE TABLE IF NOT EXISTS "Receivables" (
                    "Id" serial PRIMARY KEY,
                    "SaleId" integer NOT NULL,
                    "CustomerId" integer NOT NULL,
                    "OriginalUSD" numeric(18,4) NOT NULL,
                    "BalanceUSD" numeric(18,4) NOT NULL,
                    "OriginalBsS" numeric(18,2) NOT NULL,
                    "HistoricalRate" numeric(18,4) NOT NULL,
                    "IssuedAtUtc" timestamp with time zone NOT NULL,
                    "DueDateUtc" timestamp with time zone NOT NULL,
                    "Status" integer NOT NULL,
                    CONSTRAINT "FK_Receivables_Sales_SaleId" FOREIGN KEY ("SaleId") REFERENCES "Sales" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_Receivables_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES "Customers" ("Id") ON DELETE RESTRICT
                );

                CREATE UNIQUE INDEX IF NOT EXISTS "IX_Receivables_SaleId"
                    ON "Receivables" ("SaleId");

                CREATE INDEX IF NOT EXISTS "IX_Receivables_CustomerId_Status"
                    ON "Receivables" ("CustomerId", "Status");

                CREATE INDEX IF NOT EXISTS "IX_Receivables_DueDateUtc_Status"
                    ON "Receivables" ("DueDateUtc", "Status");

                CREATE TABLE IF NOT EXISTS "ReceivablePayments" (
                    "Id" serial PRIMARY KEY,
                    "CustomerId" integer NOT NULL,
                    "AmountUSD" numeric(18,4) NOT NULL,
                    "AmountBsS" numeric(18,2) NOT NULL,
                    "PaymentRate" numeric(18,4) NOT NULL,
                    "FxDifferenceBsS" numeric(18,2) NOT NULL,
                    "CashTransactionId" integer NULL,
                    "PaymentMethodId" integer NULL,
                    "ReferenceNumber" character varying(100) NULL,
                    "ReceivedAtUtc" timestamp with time zone NOT NULL,
                    "IdempotencyKey" character varying(100) NULL,
                    "ReceivedByUserId" integer NULL,
                    CONSTRAINT "FK_ReceivablePayments_Customers_CustomerId" FOREIGN KEY ("CustomerId") REFERENCES "Customers" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_ReceivablePayments_CashTransactions_CashTransactionId" FOREIGN KEY ("CashTransactionId") REFERENCES "CashTransactions" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_ReceivablePayments_PaymentMethods_PaymentMethodId" FOREIGN KEY ("PaymentMethodId") REFERENCES "PaymentMethods" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_ReceivablePayments_Users_ReceivedByUserId" FOREIGN KEY ("ReceivedByUserId") REFERENCES "Users" ("Id") ON DELETE RESTRICT
                );

                CREATE UNIQUE INDEX IF NOT EXISTS "IX_ReceivablePayments_IdempotencyKey"
                    ON "ReceivablePayments" ("IdempotencyKey")
                    WHERE "IdempotencyKey" IS NOT NULL;

                CREATE INDEX IF NOT EXISTS "IX_ReceivablePayments_CashTransactionId"
                    ON "ReceivablePayments" ("CashTransactionId");

                CREATE INDEX IF NOT EXISTS "IX_ReceivablePayments_CustomerId_ReceivedAtUtc"
                    ON "ReceivablePayments" ("CustomerId", "ReceivedAtUtc");

                CREATE INDEX IF NOT EXISTS "IX_ReceivablePayments_PaymentMethodId"
                    ON "ReceivablePayments" ("PaymentMethodId");

                CREATE INDEX IF NOT EXISTS "IX_ReceivablePayments_ReceivedByUserId"
                    ON "ReceivablePayments" ("ReceivedByUserId");

                CREATE TABLE IF NOT EXISTS "CreditPaymentAllocations" (
                    "Id" serial PRIMARY KEY,
                    "ReceivablePaymentId" integer NOT NULL,
                    "ReceivableId" integer NOT NULL,
                    "AmountUSD" numeric(18,4) NOT NULL,
                    "AmountBsS" numeric(18,2) NOT NULL,
                    "FxDifferenceBsS" numeric(18,2) NOT NULL,
                    CONSTRAINT "FK_CreditPaymentAllocations_ReceivablePayments_ReceivablePaymentId" FOREIGN KEY ("ReceivablePaymentId") REFERENCES "ReceivablePayments" ("Id") ON DELETE RESTRICT,
                    CONSTRAINT "FK_CreditPaymentAllocations_Receivables_ReceivableId" FOREIGN KEY ("ReceivableId") REFERENCES "Receivables" ("Id") ON DELETE RESTRICT
                );

                CREATE UNIQUE INDEX IF NOT EXISTS "IX_CreditPaymentAllocations_ReceivablePaymentId_ReceivableId"
                    ON "CreditPaymentAllocations" ("ReceivablePaymentId", "ReceivableId");

                CREATE INDEX IF NOT EXISTS "IX_CreditPaymentAllocations_ReceivableId"
                    ON "CreditPaymentAllocations" ("ReceivableId");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DROP TABLE IF EXISTS "CreditPaymentAllocations";
                DROP TABLE IF EXISTS "ReceivablePayments";
                DROP TABLE IF EXISTS "Receivables";

                DO $$
                BEGIN
                    IF to_regclass('"Customers"') IS NOT NULL THEN
                        ALTER TABLE "Customers" DROP COLUMN IF EXISTS "CreditNotes";
                        ALTER TABLE "Customers" DROP COLUMN IF EXISTS "CreditStatus";
                        ALTER TABLE "Customers" DROP COLUMN IF EXISTS "CreditGraceDays";
                        ALTER TABLE "Customers" DROP COLUMN IF EXISTS "PaymentTermDays";
                        ALTER TABLE "Customers" DROP COLUMN IF EXISTS "IsCreditEnabled";
                    END IF;
                END $$;
                """);
        }
    }
}
