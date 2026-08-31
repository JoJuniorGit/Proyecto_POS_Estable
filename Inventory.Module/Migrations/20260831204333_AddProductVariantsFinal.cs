using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddProductVariantsFinal : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Products_SKU",
                table: "Products");

            migrationBuilder.AlterColumn<decimal>(
                name: "Quantity",
                table: "StockReservations",
                type: "numeric(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<decimal>(
                name: "QuantityChange",
                table: "StockMovements",
                type: "numeric(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<decimal>(
                name: "NewStockLevel",
                table: "StockMovements",
                type: "numeric(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<decimal>(
                name: "StockQuantity",
                table: "Products",
                type: "numeric(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<decimal>(
                name: "ReservedQuantity",
                table: "Products",
                type: "numeric(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<decimal>(
                name: "PriceBsS",
                table: "Products",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AlterColumn<decimal>(
                name: "LowStockThreshold",
                table: "Products",
                type: "numeric(18,3)",
                precision: 18,
                scale: 3,
                nullable: false,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<decimal>(
                name: "LastConversionRate",
                table: "Products",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric");

            migrationBuilder.AddColumn<string>(
                name: "GroupKey",
                table: "Products",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsGroupHeader",
                table: "Products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ParentProductId",
                table: "Products",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_Active_Deleted_Name",
                table: "Products",
                columns: new[] { "IsActive", "IsDeleted", "Name" },
                filter: "\"IsActive\" = true AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_Products_GroupActiveName",
                table: "Products",
                columns: new[] { "IsGroupHeader", "IsActive", "IsDeleted", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Products_GroupKey",
                table: "Products",
                column: "GroupKey",
                filter: "\"GroupKey\" IS NOT NULL AND \"IsDeleted\" = false");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Name",
                table: "Products",
                column: "Name");

            migrationBuilder.CreateIndex(
                name: "IX_Products_Parent_Active_Deleted",
                table: "Products",
                columns: new[] { "ParentProductId", "IsActive", "IsDeleted" },
                filter: "\"ParentProductId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Products_SKU",
                table: "Products",
                column: "SKU",
                unique: true,
                filter: "\"IsDeleted\" = false");

            migrationBuilder.AddForeignKey(
                name: "FK_Products_Products_ParentProductId",
                table: "Products",
                column: "ParentProductId",
                principalTable: "Products",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Products_Products_ParentProductId",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_Active_Deleted_Name",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_GroupActiveName",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_GroupKey",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_Name",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_Parent_Active_Deleted",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_SKU",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "GroupKey",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "IsGroupHeader",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ParentProductId",
                table: "Products");

            migrationBuilder.AlterColumn<int>(
                name: "Quantity",
                table: "StockReservations",
                type: "integer",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,3)",
                oldPrecision: 18,
                oldScale: 3);

            migrationBuilder.AlterColumn<int>(
                name: "QuantityChange",
                table: "StockMovements",
                type: "integer",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,3)",
                oldPrecision: 18,
                oldScale: 3);

            migrationBuilder.AlterColumn<int>(
                name: "NewStockLevel",
                table: "StockMovements",
                type: "integer",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,3)",
                oldPrecision: 18,
                oldScale: 3);

            migrationBuilder.AlterColumn<int>(
                name: "StockQuantity",
                table: "Products",
                type: "integer",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,3)",
                oldPrecision: 18,
                oldScale: 3);

            migrationBuilder.AlterColumn<int>(
                name: "ReservedQuantity",
                table: "Products",
                type: "integer",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,3)",
                oldPrecision: 18,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "PriceBsS",
                table: "Products",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            migrationBuilder.AlterColumn<int>(
                name: "LowStockThreshold",
                table: "Products",
                type: "integer",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,3)",
                oldPrecision: 18,
                oldScale: 3);

            migrationBuilder.AlterColumn<decimal>(
                name: "LastConversionRate",
                table: "Products",
                type: "numeric",
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)",
                oldPrecision: 18,
                oldScale: 4);

            migrationBuilder.CreateIndex(
                name: "IX_Products_SKU",
                table: "Products",
                column: "SKU",
                unique: true);
        }
    }
}
