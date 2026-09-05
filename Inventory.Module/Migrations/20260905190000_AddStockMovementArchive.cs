using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Inventory.Module.Data;

#nullable disable

namespace Inventory.Module.Migrations
{
    [DbContext(typeof(InventoryDbContext))]
    [Migration("20260905190000_AddStockMovementArchive")]
    public partial class AddStockMovementArchive : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StockMovements_Archive",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OriginalMovementId = table.Column<int>(type: "integer", nullable: false),
                    ProductId = table.Column<int>(type: "integer", nullable: false),
                    QuantityChange = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    NewStockLevel = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    Reason = table.Column<string>(type: "text", nullable: false),
                    MovementDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<string>(type: "text", nullable: true),
                    ArchivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockMovements_Archive", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_Archive_OriginalMovementId",
                table: "StockMovements_Archive",
                column: "OriginalMovementId");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_Archive_MovementDate",
                table: "StockMovements_Archive",
                column: "MovementDate");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StockMovements_Archive");
        }
    }
}
