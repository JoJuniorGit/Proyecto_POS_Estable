using Core.DTOs;

namespace Sales.Module.DTOs.Profitability;

/// <summary>
/// Paso 14 (F2-S1/S4): respuesta completa del Estado de Resultados. La ventana es un intervalo
/// UTC estricto [FromUtc, ToUtc) y la moneda de reporte sigue el régimen monetario vigente
/// (USD en Dual; primaria en Single).
/// </summary>
public sealed record ProfitabilitySummaryDto
{
    /// <summary>
    /// Paso 14 (F2-S4): cuando el filtro es por categoría o producto, los gastos globales de
    /// operación se informan sin prorrateo artificial entre artículos.
    /// </summary>
    public const string GlobalCostsNoteText =
        "PettyCashExpenses, ServiceCommissions y PaymentMethodFees son gastos/ingresos globales de operación y no se prorratean artificialmente por artículo.";

    public DateTime FromUtc { get; init; }

    public DateTime ToUtc { get; init; }

    public string TimeZoneId { get; init; } = string.Empty;

    public MonetaryMode MonetaryMode { get; init; } = MonetaryMode.Dual;

    public string ReportCurrencyCode { get; init; } = "USD";

    public string ReportCurrencySymbol { get; init; } = "$";

    public bool IsSingleCurrency { get; init; }

    public DateTime GeneratedAtUtc { get; init; }

    public string GlobalCostsNote { get; init; } = GlobalCostsNoteText;

    public ProfitabilityAccruedDto Accrued { get; init; } = new();

    public ProfitabilityRealizedDto Realized { get; init; } = new();

    public ProfitabilityCoverageDto Coverage { get; init; } = new();

    public ProfitabilityBreakdownDto Breakdown { get; init; } = new();

    public IReadOnlyList<ProfitabilityAlertDto> Alerts { get; init; } = [];
}
