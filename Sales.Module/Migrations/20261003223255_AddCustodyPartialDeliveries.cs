using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddCustodyPartialDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "DeliveredQuantity",
                table: "SaleItems",
                type: "numeric(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.Sql("""
                UPDATE "SaleItems" AS item
                SET "DeliveredQuantity" = item."Quantity"
                FROM "Sales" AS sale
                WHERE sale."Id" = item."SaleId"
                  AND sale."DeliveryStatus" = 0;
                """);

            migrationBuilder.CreateTable(
                name: "SaleDeliveries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SaleId = table.Column<int>(type: "integer", nullable: false),
                    DeliveredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DeliveredByUserId = table.Column<int>(type: "integer", nullable: true),
                    DeliveredByName = table.Column<string>(type: "text", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaleDeliveries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SaleDeliveries_Sales_SaleId",
                        column: x => x.SaleId,
                        principalTable: "Sales",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SaleDeliveries_Users_DeliveredByUserId",
                        column: x => x.DeliveredByUserId,
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SaleDeliveryItems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SaleDeliveryId = table.Column<int>(type: "integer", nullable: false),
                    SaleItemId = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    ProductName = table.Column<string>(type: "text", nullable: false),
                    QuantityDelivered = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SaleDeliveryItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SaleDeliveryItems_SaleDeliveries_SaleDeliveryId",
                        column: x => x.SaleDeliveryId,
                        principalTable: "SaleDeliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SaleDeliveryItems_SaleItems_SaleItemId",
                        column: x => x.SaleItemId,
                        principalTable: "SaleItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SaleDeliveries_DeliveredByUserId",
                table: "SaleDeliveries",
                column: "DeliveredByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleDeliveries_SaleId",
                table: "SaleDeliveries",
                column: "SaleId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleDeliveryItems_SaleDeliveryId",
                table: "SaleDeliveryItems",
                column: "SaleDeliveryId");

            migrationBuilder.CreateIndex(
                name: "IX_SaleDeliveryItems_SaleItemId",
                table: "SaleDeliveryItems",
                column: "SaleItemId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SaleDeliveryItems");

            migrationBuilder.DropTable(
                name: "SaleDeliveries");

            migrationBuilder.DropColumn(
                name: "DeliveredQuantity",
                table: "SaleItems");
        }
    }
}
