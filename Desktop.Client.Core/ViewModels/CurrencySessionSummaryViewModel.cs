using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

/// <summary>
/// S1/S2: fila de resumen de caja para una moneda activa. Cada monto se formatea en la divisa
/// nominal de la fila y con el formato numérico activo; no hay conversiones cruzadas.
/// </summary>
public sealed class CurrencySessionSummaryViewModel
{
    public string CurrencyCode { get; init; } = string.Empty;
    public string CurrencySymbol { get; init; } = string.Empty;
    public decimal OpeningBalance { get; init; }
    public decimal TotalPhysicalInflows { get; init; }
    public decimal TotalPhysicalOutflows { get; init; }
    public decimal ExpectedCashBalance { get; init; }
    public bool IsLocal { get; init; }

    public string DisplayLabel => string.Equals(CurrencyCode, "VES", System.StringComparison.Ordinal)
        ? "Bs.S"
        : CurrencyCode;

    public string FormattedOpening => CurrencyDisplay.Number(OpeningBalance, 2);
    public string FormattedInflows => CurrencyDisplay.Number(TotalPhysicalInflows, 2);
    public string FormattedOutflows => CurrencyDisplay.Number(TotalPhysicalOutflows, 2);
    public string FormattedExpected => CurrencyDisplay.Number(ExpectedCashBalance, 2);
}
