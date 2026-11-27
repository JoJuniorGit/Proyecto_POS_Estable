using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Sales.Module.Entities;

public class DailyClosure
{
    public int Id { get; set; }

    public DateTime ClosureDate { get; set; } = DateTime.UtcNow;

    [MaxLength(100)]
    public string? UserId { get; set; } = "Admin";

    [Column(TypeName = "decimal(18,4)")]
    public decimal ExchangeRate { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalExpectedBsS { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalActualBsS { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal TotalDifferenceBsS { get; set; }

    [MaxLength(500)]
    public string? Observation { get; set; }

    /// <summary>
    /// Residual 8.187-8.188: arqueo fisico USD del cierre diario. El esperado se congela desde el
    /// bucket USD de la sesion de caja al momento del cierre; el contado viaja opcional desde el
    /// cliente. Los tres campos permanecen null cuando no se informo conteo USD (compatibilidad).
    /// </summary>
    [Column(TypeName = "decimal(18,4)")]
    public decimal? ExpectedClosingBalanceUsd { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal? ActualClosingBalanceUsd { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal? DifferenceUsd { get; set; }

    /// <summary>
    /// Quick win 8.188 (Paso 13): sesion de caja que este cierre cierra. Es el ancla exacta del
    /// renglon informativo OPEX (gastos Active del turno) al releer el cierre; los cierres legados
    /// sin sesion quedan en null y usan el fallback de la ventana del cierre anterior.
    /// </summary>
    public int? SessionId { get; set; }

    public CashDrawerSession? Session { get; set; }

    public List<ClosureDetail> Details { get; set; } = new();
}
