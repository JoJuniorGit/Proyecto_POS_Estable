using System;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Inventory.Module.Migrations
{
    [DbContext(typeof(InventoryDbContext))]
    [Migration("20260425000000_AddSupplierOcrTemplates")]
    public partial class AddSupplierOcrTemplates : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SupplierOcrTemplates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SupplierId = table.Column<int>(type: "integer", nullable: false),
                    RotationDegrees = table.Column<double>(type: "double precision", nullable: false),
                    ContrastFactor = table.Column<double>(type: "double precision", nullable: false),
                    BrightnessDelta = table.Column<double>(type: "double precision", nullable: false),
                    BinarizationThreshold = table.Column<int>(type: "integer", nullable: false),
                    RoiX = table.Column<double>(type: "double precision", nullable: true),
                    RoiY = table.Column<double>(type: "double precision", nullable: true),
                    RoiWidth = table.Column<double>(type: "double precision", nullable: true),
                    RoiHeight = table.Column<double>(type: "double precision", nullable: true),
                    HeaderAnchorKeywords = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FooterAnchorKeywords = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ProductColumnPosition = table.Column<int>(type: "integer", nullable: true),
                    QuantityColumnPosition = table.Column<int>(type: "integer", nullable: true),
                    UnitCostColumnPosition = table.Column<int>(type: "integer", nullable: true),
                    SupplierCodeColumnPosition = table.Column<int>(type: "integer", nullable: true),
                    CostAmountRule = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "UnitCost"),
                    DecimalSeparator = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false, defaultValue: "Auto"),
                    CharacterReplacementsJson = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    FactorExtractionPattern = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SupplierOcrTemplates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SupplierOcrTemplates_Suppliers_SupplierId",
                        column: x => x.SupplierId,
                        principalTable: "Suppliers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SupplierOcrTemplates_SupplierId",
                table: "SupplierOcrTemplates",
                column: "SupplierId",
                unique: true);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SupplierOcrTemplates");
        }
    }
}
