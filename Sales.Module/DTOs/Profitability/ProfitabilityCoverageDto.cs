namespace Sales.Module.DTOs.Profitability;

/// <summary>
/// Paso 14 (F2-S1/S7): cobertura de datos históricos. Expone qué porción del ingreso y de los
/// cobros tiene costo/comisión conocidos, sin inventar ni backfillear valores faltantes.
/// </summary>
public sealed record ProfitabilityCoverageDto
{
    public decimal CostCoveragePercent { get; init; }

    public decimal KnownCostRevenue { get; init; }

    public decimal KnownCogs { get; init; }

    public decimal UnknownCostRevenue { get; init; }

    public int UnknownCostLinesCount { get; init; }

    public decimal FeeCoveragePercent { get; init; }

    public decimal KnownFeePaymentAmount { get; init; }

    public decimal UnknownFeePaymentAmount { get; init; }

    public int UnknownFeePaymentsCount { get; init; }
}
