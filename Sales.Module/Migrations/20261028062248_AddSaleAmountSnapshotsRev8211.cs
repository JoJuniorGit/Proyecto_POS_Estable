using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddSaleAmountSnapshotsRev8211 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AppliedRateSource",
                table: "Sales",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "PrimaryAmount",
                table: "Sales",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "SecondaryAmount",
                table: "Sales",
                type: "numeric(18,4)",
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "OriginalAmount",
                table: "SalePayments",
                type: "numeric(18,4)",
                nullable: true);

            // 8.211-T3c2 (S6): backfill idempotente de las filas legacy. Los importes primario y
            // secundario copian los totales canónicos ya persistidos; el importe original del pago
            // se deriva de la moneda snapshot (USD -> Amount, VES -> AmountBsS, resto -> Amount).
            // AppliedRateSource queda NULL en las filas legacy: solo describe ventas nuevas.
            migrationBuilder.Sql(
                """
                UPDATE "Sales"
                SET "PrimaryAmount" = "TotalUSD",
                    "SecondaryAmount" = "TotalBsS"
                WHERE "PrimaryAmount" IS NULL OR "SecondaryAmount" IS NULL;

                UPDATE "SalePayments"
                SET "OriginalAmount" = CASE
                    WHEN "CurrencyCode" = 'USD' THEN "Amount"
                    WHEN "CurrencyCode" IN ('Bs.S', 'BsS', 'VES') THEN "AmountBsS"
                    ELSE "Amount"
                END
                WHERE "OriginalAmount" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AppliedRateSource",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "PrimaryAmount",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "SecondaryAmount",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "OriginalAmount",
                table: "SalePayments");
        }
    }
}
