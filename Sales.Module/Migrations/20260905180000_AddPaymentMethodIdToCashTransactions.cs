using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Sales.Module.Data;

#nullable disable

namespace Sales.Module.Migrations
{
    [DbContext(typeof(SalesDbContext))]
    [Migration("20260905180000_AddPaymentMethodIdToCashTransactions")]
    public partial class AddPaymentMethodIdToCashTransactions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PaymentMethodId",
                table: "CashTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_CashTransactions_PaymentMethodId",
                table: "CashTransactions",
                column: "PaymentMethodId");

            migrationBuilder.AddForeignKey(
                name: "FK_CashTransactions_PaymentMethods_PaymentMethodId",
                table: "CashTransactions",
                column: "PaymentMethodId",
                principalTable: "PaymentMethods",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_CashTransactions_PaymentMethods_PaymentMethodId",
                table: "CashTransactions");

            migrationBuilder.DropIndex(
                name: "IX_CashTransactions_PaymentMethodId",
                table: "CashTransactions");

            migrationBuilder.DropColumn(
                name: "PaymentMethodId",
                table: "CashTransactions");
        }
    }
}
