namespace Sales.Module.DTOs.Profitability;

/// <summary>
/// Paso 14 (F2-S2): bloque devengado del período. El margen bruto excluye el ingreso con costo
/// desconocido; los gastos globales de operación se muestran como renglones separados.
/// </summary>
public sealed record ProfitabilityAccruedDto
{
    public decimal GrossSales { get; init; }

    public decimal Cancellations { get; init; }

    public decimal Returns { get; init; }

    public decimal NetSales { get; init; }

    public decimal KnownCostRevenue { get; init; }

    public decimal UnknownCostRevenue { get; init; }

    public decimal Cogs { get; init; }

    public decimal GrossProfit { get; init; }

    public decimal GrossMarginPercent { get; init; }

    public decimal ServiceCommissions { get; init; }

    public decimal FxDifferenceInformational { get; init; }

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
