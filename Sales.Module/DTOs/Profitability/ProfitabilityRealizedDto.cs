namespace Sales.Module.DTOs.Profitability;

/// <summary>
/// Paso 14 (F2-S3): bloque percibido en caja. El CMV se prorratea por factura con cobertura de
/// costo completa y el ingreso con costo desconocido queda segregado fuera del margen realizado.
/// </summary>
public sealed record ProfitabilityRealizedDto
{
    public decimal CollectedRevenue { get; init; }

    public decimal KnownCostCollectedRevenue { get; init; }

    public decimal UnknownCostCollectedRevenue { get; init; }

    public decimal RealizedCogs { get; init; }

    public decimal RealizedGrossProfit { get; init; }

    public decimal RealizedGrossMarginPercent { get; init; }

    public decimal ServiceCommissions { get; init; }

    public decimal FxDifference { get; init; }

    public decimal PettyCashExpenses { get; init; }

    public decimal PaymentMethodFees { get; init; }

    /// <summary>
    /// P0 (S3): gastos operativos mensuales prorrateados por dias calendario activos en la
    /// ventana. Es un costo global del periodo y se resta de <see cref="NetProfit"/>.
    /// </summary>
    public decimal OperatingExpenses { get; init; }

    public decimal NetProfit { get; init; }

    public decimal NetMarginPercent { get; init; }
}
