using Sales.Module.Entities;

namespace Sales.Module.DTOs.Profitability;

/// <summary>
/// Paso 14 (F2-S1/S4): filtros combinables del Estado de Resultados. Las fechas son de calendario
/// local; el servicio las traduce a la ventana UTC [fromUtc, toUtc) antes de consultar.
/// </summary>
public sealed class ProfitabilityFilterDto
{
    public ProfitabilityPeriodPreset Preset { get; set; } = ProfitabilityPeriodPreset.Today;

    public DateOnly? FromDate { get; set; }

    public DateOnly? ToDate { get; set; }

    public IReadOnlyList<int>? CategoryIds { get; set; }

    public bool IncludeCategorySubtree { get; set; } = true;

    public IReadOnlyList<int>? ProductIds { get; set; }

    public IReadOnlyList<int>? PaymentMethodIds { get; set; }

    public int? CashierId { get; set; }

    public SaleType? SaleType { get; set; }

    public string? PriceListType { get; set; }

    public ProfitabilityGroupBy GroupBy { get; set; } = ProfitabilityGroupBy.Day;
}
