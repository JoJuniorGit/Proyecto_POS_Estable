using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Module.Migrations
{
    /// <inheritdoc />
    public partial class AddProductVariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ParentProductId",
                table: "Products",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsGroupHeader",
                table: "Products",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "GroupKey",
                table: "Products",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Products_Parent_Active_Deleted",
                table: "Products",
                columns: new[] { "ParentProductId", "IsActive", "IsDeleted" },
                filter: "\"ParentProductId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Products_GroupActiveName",
                table: "Products",
                columns: new[] { "IsGroupHeader", "IsActive", "IsDeleted", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_Products_GroupKey",
                table: "Products",
                column: "GroupKey",
                filter: "\"GroupKey\" IS NOT NULL AND \"IsDeleted\" = false");

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
                name: "IX_Products_Parent_Active_Deleted",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_GroupActiveName",
                table: "Products");

            migrationBuilder.DropIndex(
                name: "IX_Products_GroupKey",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "ParentProductId",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "IsGroupHeader",
                table: "Products");

            migrationBuilder.DropColumn(
                name: "GroupKey",
                table: "Products");
        }
    }
}
