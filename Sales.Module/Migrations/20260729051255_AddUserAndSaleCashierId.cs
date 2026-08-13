using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddUserAndSaleCashierId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "CashierId",
                table: "Sales",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "Users",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Cedula = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: false),
                    FullName = table.Column<string>(type: "text", nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Users", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "Users",
                columns: new[] { "Id", "Cedula", "CreatedAt", "FullName", "IsActive", "Name", "PasswordHash", "PhoneNumber", "Role", "UpdatedAt", "Username" },
                values: new object[] { 1, "12345678", new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc), "", true, "Administrador", "", null, 0, null, "admin" });

            migrationBuilder.CreateIndex(
                name: "IX_Sales_CashierId",
                table: "Sales",
                column: "CashierId");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Cedula",
                table: "Users",
                column: "Cedula",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Sales_Users_CashierId",
                table: "Sales",
                column: "CashierId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Sales_Users_CashierId",
                table: "Sales");

            migrationBuilder.DropTable(
                name: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Sales_CashierId",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "CashierId",
                table: "Sales");
        }
    }
}
