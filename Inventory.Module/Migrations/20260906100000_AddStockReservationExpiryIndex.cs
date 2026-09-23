using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Inventory.Module.Data;

#nullable disable

namespace Inventory.Module.Migrations
{
    [DbContext(typeof(InventoryDbContext))]
    [Migration("20260906100000_AddStockReservationExpiryIndex")]
    public partial class AddStockReservationExpiryIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                CREATE INDEX IF NOT EXISTS ""IX_StockReservations_ExpiryDate_IsConfirmed""
                    ON ""StockReservations"" (""ExpiryDate"", ""IsConfirmed"");
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""IX_StockReservations_ExpiryDate_IsConfirmed"";
            ");
        }
    }
}
