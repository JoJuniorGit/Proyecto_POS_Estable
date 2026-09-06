using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Inventory.Module.Migrations
{
    /// <summary>
    /// Migración de sincronización del modelo (no operativa).
    ///
    /// Registra en el snapshot del modelo el token de concurrencia `xmin` de
    /// PostgreSQL para `Product` (hallazgo 8.2-A1) y sana la desincronización del
    /// snapshot (faltaba el índice `IX_StockReservations_ExpiryDate_IsConfirmed`,
    /// creado por SQL crudo en `20260906100000_AddStockReservationExpiryIndex`).
    ///
    /// `xmin` es una pseudo-columna de sistema que PostgreSQL expone automáticamente
    /// en cada fila, por lo que NO puede ni debe añadirse con DDL (`ALTER TABLE ADD
    /// COLUMN xmin` es inválido). Las tablas e índices ya existen en la base de datos.
    /// Por ello `Up()` y `Down()` son no-op: el snapshot queda alineado con el modelo
    /// sin riesgo de recrear o destruir objetos.
    /// </summary>
    public partial class AddXminConcurrencyTokenToProduct : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
        }
    }
}
