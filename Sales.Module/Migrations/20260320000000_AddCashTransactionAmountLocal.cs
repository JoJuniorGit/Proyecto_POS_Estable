using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    public partial class AddCashTransactionAmountLocal : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AmountLocal",
                table: "CashTransactions",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            // Backfill existing rows so the cash register can compute balances without
            // needing USD -> Bs.S recalculation.
            migrationBuilder.Sql(
                "UPDATE \"CashTransactions\" SET \"AmountLocal\" = ROUND(\"AmountUsd\" * \"ExchangeRate\", 2);");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AmountLocal",
                table: "CashTransactions");
        }
    }
}

