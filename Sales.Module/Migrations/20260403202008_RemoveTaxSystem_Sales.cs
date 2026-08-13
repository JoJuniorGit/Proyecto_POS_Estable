using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTaxSystem_Sales : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TaxAmount",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "TaxAmountBsS",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "TaxAmount",
                table: "SaleItems");

            migrationBuilder.DropColumn(
                name: "TaxAmountBsS",
                table: "SaleItems");

            migrationBuilder.DropColumn(
                name: "TaxRate",
                table: "SaleItems");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "TaxAmount",
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
                name: "TaxAmount",
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
                name: "TaxRate",
                table: "SaleItems",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);
        }
    }
}
