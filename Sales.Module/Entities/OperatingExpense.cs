using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Sales.Module.Entities;

/// <summary>
/// P0 (S3): gasto operativo mensual recurrente (p. ej. mantenimiento de cuenta TDC y gastos
/// mensuales del POS). Es un costo global del negocio: nunca se suma al cliente ni al checkout, y
/// el Estado de Resultados lo prorratea por dias calendario activos dentro de la ventana del
/// reporte. No se prorratea por producto ni por medio de pago cuando hay filtros.
/// </summary>
public class OperatingExpense
{
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    public string Description { get; set; } = string.Empty;

    /// <summary>Monto mensual de referencia en USD. Es la base del prorrateo en reportes Dual.</summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal MonthlyAmountUsd { get; set; }

    /// <summary>
    /// Monto mensual en moneda local. Opcional: si falta, el reporte Single no inventa una tasa ni
    /// un monto; el gasto vale 0 para esa moneda.
    /// </summary>
    [Column(TypeName = "decimal(18,2)")]
    public decimal? MonthlyAmountLocal { get; set; }

    [MaxLength(10)]
    public string? LocalCurrencyCode { get; set; }

    /// <summary>Asociacion opcional al medio de pago que origina el cargo (p. ej. TDC).</summary>
    public int? PaymentMethodId { get; set; }

    public PaymentMethod? PaymentMethod { get; set; }

    public DateOnly EffectiveFromDate { get; set; }

    public DateOnly? EffectiveToDate { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
