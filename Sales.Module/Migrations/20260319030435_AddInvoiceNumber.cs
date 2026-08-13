using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "InvoiceNumber",
                table: "Sales",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Sales_InvoiceNumber",
                table: "Sales",
                column: "InvoiceNumber",
                unique: true,
                filter: "\"InvoiceNumber\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Sales_InvoiceNumber",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "InvoiceNumber",
                table: "Sales");
        }
    }
}
