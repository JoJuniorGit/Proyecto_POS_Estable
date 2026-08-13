using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddPersistentBsSColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "SubtotalBsS",
                table: "Sales",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmountBsS",
                table: "Sales",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TotalBsS",
                table: "Sales",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "AmountBsS",
                table: "SalePayments",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "SubtotalBsS",
                table: "SaleItems",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmountBsS",
                table: "SaleItems",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "UnitPriceBsS",
                table: "SaleItems",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "AmountLocal",
                table: "CashTransactions",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "SubtotalBsS",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "TaxAmountBsS",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "TotalBsS",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "AmountBsS",
                table: "SalePayments");

            migrationBuilder.DropColumn(
                name: "SubtotalBsS",
                table: "SaleItems");

            migrationBuilder.DropColumn(
                name: "TaxAmountBsS",
                table: "SaleItems");

            migrationBuilder.DropColumn(
                name: "UnitPriceBsS",
                table: "SaleItems");

            migrationBuilder.DropColumn(
                name: "AmountLocal",
                table: "CashTransactions");
        }
    }
}
