using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Sales.Module.Data;

#nullable disable

namespace Sales.Module.Migrations
{
    [DbContext(typeof(SalesDbContext))]
    [Migration("20260905140000_AddIsDeletedAndUniqueNameToPaymentMethod")]
    public partial class AddIsDeletedAndUniqueNameToPaymentMethod : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 1. Agregar columna IsDeleted para soporte de eliminación inteligente (soft delete)
            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "PaymentMethods",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // 2. Crear secuencia para asignación atómica y segura de DisplayOrder bajo concurrencia
            migrationBuilder.CreateSequence<int>(
                name: "paymentmethod_displayorder_seq",
                startValue: 1L);

            migrationBuilder.Sql(@"
                SELECT setval('paymentmethod_displayorder_seq', GREATEST(COALESCE((SELECT MAX(""DisplayOrder"") FROM ""PaymentMethods""), 0) + 1, 1), false);
            ");

            // 3. Índice funcional único condicional para unicidad insensible a mayúsculas entre métodos no eliminados
            migrationBuilder.Sql(@"
                CREATE UNIQUE INDEX IF NOT EXISTS ""IX_PaymentMethods_Name_Unique_NotDeleted""
                ON ""PaymentMethods"" (LOWER(TRIM(""Name"")))
                WHERE ""IsDeleted"" = false;
            ");

            // 4. Asegurar que las filas existentes queden con IsDeleted = false
            migrationBuilder.Sql(@"
                UPDATE ""PaymentMethods"" SET ""IsDeleted"" = false WHERE ""IsDeleted"" IS NULL;
            ");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                DROP INDEX IF EXISTS ""IX_PaymentMethods_Name_Unique_NotDeleted"";
            ");

            migrationBuilder.DropSequence(
                name: "paymentmethod_displayorder_seq");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "PaymentMethods");
        }
    }
}
