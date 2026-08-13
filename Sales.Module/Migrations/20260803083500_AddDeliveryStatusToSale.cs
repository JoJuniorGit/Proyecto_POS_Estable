using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Sales.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddDeliveryStatusToSale : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsProductDelivered",
                table: "Sales");

            migrationBuilder.AddColumn<int>(
                name: "DeliveryStatus",
                table: "Sales",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<DateTime>(
                name: "PickupDate",
                table: "Sales",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeliveryStatus",
                table: "Sales");

            migrationBuilder.DropColumn(
                name: "PickupDate",
                table: "Sales");

            migrationBuilder.AddColumn<bool>(
                name: "IsProductDelivered",
                table: "Sales",
                type: "boolean",
                nullable: false,
                defaultValue: true);
        }
    }
}
