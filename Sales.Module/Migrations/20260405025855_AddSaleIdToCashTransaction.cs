using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddSaleIdToCashTransaction : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SaleId",
                table: "CashTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashTransactions_SaleId",
                table: "CashTransactions",
                column: "SaleId");

            migrationBuilder.AddForeignKey(
                name: "FK_CashTransactions_Sales_SaleId",
                table: "CashTransactions",
                column: "SaleId",
                principalTable: "Sales",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CashTransactions_Sales_SaleId",
                table: "CashTransactions");

            migrationBuilder.DropIndex(
                name: "IX_CashTransactions_SaleId",
                table: "CashTransactions");

            migrationBuilder.DropColumn(
                name: "SaleId",
                table: "CashTransactions");
        }
    }
}
