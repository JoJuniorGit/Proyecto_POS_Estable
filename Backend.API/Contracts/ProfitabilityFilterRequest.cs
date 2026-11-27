using Sales.Module.DTOs.Profitability;
using Sales.Module.Entities;

namespace Backend.API.Contracts;

/// <summary>
/// Paso 14 (F3-S1): filtros query-string del Estado de Resultados. Las fechas son de calendario
/// local y el servicio las traduce a la ventana UTC [fromUtc, toUtc) de la Zona Horaria Global.
/// </summary>
public sealed class ProfitabilityFilterRequest
{
    public ProfitabilityPeriodPreset Preset { get; set; } = ProfitabilityPeriodPreset.Today;

    public DateOnly? FromDate { get; set; }

    public DateOnly? ToDate { get; set; }

    public int[]? CategoryIds { get; set; }

    public bool IncludeCategorySubtree { get; set; } = true;

    public int[]? ProductIds { get; set; }

    public int[]? PaymentMethodIds { get; set; }

    public int? CashierId { get; set; }

    public SaleType? SaleType { get; set; }

    public string? PriceListType { get; set; }

    public ProfitabilityGroupBy GroupBy { get; set; } = ProfitabilityGroupBy.Day;

    public ProfitabilityFilterDto ToFilter() => new()
    {
        Preset = Preset,
        FromDate = FromDate,
        ToDate = ToDate,
        CategoryIds = CategoryIds,
        IncludeCategorySubtree = IncludeCategorySubtree,
        ProductIds = ProductIds,
        PaymentMethodIds = PaymentMethodIds,
        CashierId = CashierId,
        SaleType = SaleType,
        PriceListType = string.IsNullOrWhiteSpace(PriceListType) ? null : PriceListType.Trim(),
        GroupBy = GroupBy
    };
}
