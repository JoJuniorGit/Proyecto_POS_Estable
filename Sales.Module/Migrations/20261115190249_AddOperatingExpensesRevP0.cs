using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddOperatingExpensesRevP0 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "OperatingExpenses",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Description = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    MonthlyAmountUsd = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    MonthlyAmountLocal = table.Column<decimal>(type: "numeric(18,2)", nullable: true),
                    LocalCurrencyCode = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: true),
                    PaymentMethodId = table.Column<int>(type: "integer", nullable: true),
                    EffectiveFromDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EffectiveToDate = table.Column<DateOnly>(type: "date", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OperatingExpenses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OperatingExpenses_PaymentMethods",
                        column: x => x.PaymentMethodId,
                        principalTable: "PaymentMethods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OperatingExpenses_IsActive_EffectiveFromDate",
                table: "OperatingExpenses",
                columns: new[] { "IsActive", "EffectiveFromDate" });

            migrationBuilder.CreateIndex(
                name: "IX_OperatingExpenses_PaymentMethodId",
                table: "OperatingExpenses",
                column: "PaymentMethodId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OperatingExpenses");
        }
    }
}
