using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    public partial class RenameToAppliedRateAndTotalUSD : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "TotalAmount",
                table: "Sales",
                newName: "TotalUSD");

            migrationBuilder.RenameColumn(
                name: "ExchangeRate",
                table: "Sales",
                newName: "AppliedRate");

            migrationBuilder.AddColumn<decimal>(
                name: "FinalPaidAmountBsS",
                table: "Sales",
                type: "numeric(18,2)",
                nullable: false,
                defaultValue: 0m);

            // Copy old TotalBsS data into the new FinalPaidAmountBsS snapshot
            migrationBuilder.Sql("UPDATE \"Sales\" SET \"FinalPaidAmountBsS\" = \"TotalBsS\";");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FinalPaidAmountBsS",
                table: "Sales");

            migrationBuilder.RenameColumn(
                name: "TotalUSD",
                table: "Sales",
                newName: "TotalAmount");

            migrationBuilder.RenameColumn(
                name: "AppliedRate",
                table: "Sales",
                newName: "ExchangeRate");
        }
    }
}
