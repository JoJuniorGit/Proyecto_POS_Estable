using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Inventory.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddCurrencyPairRatesRev8211 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CurrencyPairRates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RateDate = table.Column<DateOnly>(type: "date", nullable: false),
                    FromCurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ToCurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    Rate = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CurrencyPairRates", x => x.Id);
                    table.CheckConstraint("CK_CurrencyPairRates_Rate_Positive", "\"Rate\" > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_CurrencyPairRates_RateDate_From_To",
                table: "CurrencyPairRates",
                columns: new[] { "RateDate", "FromCurrencyCode", "ToCurrencyCode" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CurrencyPairRates");
        }
    }
}
