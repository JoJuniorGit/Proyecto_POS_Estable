using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddDailyClosure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DailyClosures",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClosureDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    TotalExpectedBsS = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    TotalActualBsS = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    TotalDifferenceBsS = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    Observation = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyClosures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ClosureDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DailyClosureId = table.Column<int>(type: "integer", nullable: false),
                    PaymentMethodId = table.Column<int>(type: "integer", nullable: false),
                    PaymentMethodName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ExpectedAmountBsS = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    ActualAmountBsS = table.Column<decimal>(type: "numeric(18,2)", nullable: false),
                    DifferenceBsS = table.Column<decimal>(type: "numeric(18,2)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClosureDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClosureDetails_DailyClosures_DailyClosureId",
                        column: x => x.DailyClosureId,
                        principalTable: "DailyClosures",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ClosureDetails_PaymentMethods_PaymentMethodId",
                        column: x => x.PaymentMethodId,
                        principalTable: "PaymentMethods",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClosureDetails_DailyClosureId",
                table: "ClosureDetails",
                column: "DailyClosureId");

            migrationBuilder.CreateIndex(
                name: "IX_ClosureDetails_PaymentMethodId",
                table: "ClosureDetails",
                column: "PaymentMethodId");

            migrationBuilder.CreateIndex(
                name: "IX_DailyClosures_ClosureDate",
                table: "DailyClosures",
                column: "ClosureDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClosureDetails");

            migrationBuilder.DropTable(
                name: "DailyClosures");
        }
    }
}
