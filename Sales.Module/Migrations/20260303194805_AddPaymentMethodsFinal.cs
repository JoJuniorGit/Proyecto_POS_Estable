using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentMethodsFinal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PaymentMethodId",
                table: "Sales",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReferenceNumber",
                table: "Sales",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PaymentMethods",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    RequiresReference = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PaymentMethods", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "PaymentMethods",
                columns: new[] { "Id", "IsActive", "Name", "RequiresReference" },
                values: new object[,]
                {
                    { 1, true, "Cash", false },
                    { 2, true, "Card", true }
                });

            migrationBuilder.CreateIndex(
                name: "IX_Sales_PaymentMethodId",
                table: "Sales",
                column: "PaymentMethodId");

            migrationBuilder.AddForeignKey(
                name: "FK_Sales_PaymentMethods_PaymentMethodId",
                table: "Sales",
                column: "PaymentMethodId",
                principalTable: "PaymentMethods",
                principalColumn: "Id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Sales_PaymentMethods_PaymentMethodId",
                table: "Sales");

            migrationBuilder.DropTable(
                name: "PaymentMethods");

            migrationBuilder.DropIndex(
                name: "IX_Sales_PaymentMethodId",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "PaymentMethodId",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "ReferenceNumber",
                table: "Sales");
        }
    }
}
