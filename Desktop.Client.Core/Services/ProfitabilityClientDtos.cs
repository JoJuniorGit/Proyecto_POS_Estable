using System;
using System.Collections.Generic;
using System.ComponentModel;

namespace Desktop.Client.Services;

/// <summary>
/// F4-WU1 (Paso 14): espejo cliente de los enums del Estado de Resultados. Los valores numéricos
/// coinciden con el contrato de la API; nunca se renumeran.
/// </summary>
public enum ProfitabilityPeriodPresetClient
{
    Today = 0,
    Yesterday = 1,
    Week = 2,
    Month = 3,
    Custom = 4
}

public enum ProfitabilityGroupByClient
{
    Hour = 0,
    Day = 1,
    Week = 2,
    Month = 3
}

public enum ProfitabilityAlertSeverityClient
{
    Info = 0,
    Warning = 1,
    Critical = 2
}

public enum ProfitabilitySaleTypeClient
{
    Merchandise = 0,
    CashAdvance = 1,
    Service = 2
}

public enum PaymentMethodFeeTypeClient
{
    Percentage = 0,
    Fixed = 1,
    PercentagePlusFixed = 2
}

/// <summary>
/// F4-WU1: filtros de la consulta de rentabilidad. Las fechas son de calendario local; el
/// backend las traduce a la ventana UTC [fromUtc, toUtc) de la Zona Horaria Global.
/// </summary>
public sealed class ProfitabilityClientFilter
{
    public ProfitabilityPeriodPresetClient Preset { get; set; } = ProfitabilityPeriodPresetClient.Today;

    public DateOnly? FromDate { get; set; }

    public DateOnly? ToDate { get; set; }

    public IReadOnlyList<int>? CategoryIds { get; set; }

    public bool IncludeCategorySubtree { get; set; } = true;

    public IReadOnlyList<int>? ProductIds { get; set; }

    public IReadOnlyList<int>? PaymentMethodIds { get; set; }

    public int? CashierId { get; set; }

    public ProfitabilitySaleTypeClient? SaleType { get; set; }

    public string? PriceListType { get; set; }

    public ProfitabilityGroupByClient GroupBy { get; set; } = ProfitabilityGroupByClient.Day;
}

/// <summary>F4-WU1: respuesta completa del Estado de Resultados.</summary>
public sealed class ProfitabilitySummaryClientDto
{
    public DateTime FromUtc { get; set; }

    public DateTime ToUtc { get; set; }

    public string TimeZoneId { get; set; } = string.Empty;

    public string ReportCurrencyCode { get; set; } = "USD";

    public string ReportCurrencySymbol { get; set; } = "$";

    public bool IsSingleCurrency { get; set; }

    public DateTime GeneratedAtUtc { get; set; }

    public string GlobalCostsNote { get; set; } = string.Empty;

    public ProfitabilityAccruedClientDto Accrued { get; set; } = new();

    public ProfitabilityRealizedClientDto Realized { get; set; } = new();

    public ProfitabilityCoverageClientDto Coverage { get; set; } = new();

    public ProfitabilityBreakdownClientDto Breakdown { get; set; } = new();

    public IReadOnlyList<ProfitabilityAlertClientDto> Alerts { get; set; } = Array.Empty<ProfitabilityAlertClientDto>();
}

public sealed class ProfitabilityAccruedClientDto
{
    public decimal GrossSales { get; set; }

    public decimal Cancellations { get; set; }

    public decimal Returns { get; set; }

    public decimal NetSales { get; set; }

    public decimal KnownCostRevenue { get; set; }

    public decimal UnknownCostRevenue { get; set; }

    public decimal Cogs { get; set; }

    public decimal GrossProfit { get; set; }

    public decimal GrossMarginPercent { get; set; }

    public decimal ServiceCommissions { get; set; }

    public decimal FxDifferenceInformational { get; set; }

    public decimal PettyCashExpenses { get; set; }

    public decimal PaymentMethodFees { get; set; }

    public decimal NetProfit { get; set; }

    public decimal NetMarginPercent { get; set; }
}

public sealed class ProfitabilityRealizedClientDto
{
    public decimal CollectedRevenue { get; set; }

    public decimal KnownCostCollectedRevenue { get; set; }

    public decimal UnknownCostCollectedRevenue { get; set; }

    public decimal RealizedCogs { get; set; }

    public decimal RealizedGrossProfit { get; set; }

    public decimal RealizedGrossMarginPercent { get; set; }

    public decimal ServiceCommissions { get; set; }

    public decimal FxDifference { get; set; }

    public decimal PettyCashExpenses { get; set; }

    public decimal PaymentMethodFees { get; set; }

    public decimal NetProfit { get; set; }

    public decimal NetMarginPercent { get; set; }
}

public sealed class ProfitabilityCoverageClientDto
{
    public decimal CostCoveragePercent { get; set; }

    public decimal KnownCostRevenue { get; set; }

    public decimal KnownCogs { get; set; }

    public decimal UnknownCostRevenue { get; set; }

    public int UnknownCostLinesCount { get; set; }

    public decimal FeeCoveragePercent { get; set; }

    public decimal KnownFeePaymentAmount { get; set; }

    public decimal UnknownFeePaymentAmount { get; set; }

    public int UnknownFeePaymentsCount { get; set; }
}

public sealed class ProfitabilityBreakdownClientDto
{
    public IReadOnlyList<ProfitabilitySeriesPointClientDto> Series { get; set; } =
        Array.Empty<ProfitabilitySeriesPointClientDto>();

    public IReadOnlyList<PaymentMethodBreakdownClientDto> PaymentMethods { get; set; } =
        Array.Empty<PaymentMethodBreakdownClientDto>();

    public IReadOnlyList<CategoryBreakdownClientDto> Categories { get; set; } =
        Array.Empty<CategoryBreakdownClientDto>();

    public IReadOnlyList<ProductBreakdownClientDto> Products { get; set; } =
        Array.Empty<ProductBreakdownClientDto>();

    public IReadOnlyList<SellerBreakdownClientDto> Sellers { get; set; } =
        Array.Empty<SellerBreakdownClientDto>();

    public IReadOnlyList<ProfitabilityAlertClientDto> Alerts { get; set; } =
        Array.Empty<ProfitabilityAlertClientDto>();
}

public sealed class ProfitabilitySeriesPointClientDto
{
    public DateTime PeriodStartUtc { get; set; }

    public string Label { get; set; } = string.Empty;

    public decimal AccruedNetProfit { get; set; }

    public decimal RealizedNetProfit { get; set; }

    public decimal AccruedNetSales { get; set; }

    public decimal RealizedCollectedRevenue { get; set; }
}

public sealed class PaymentMethodBreakdownClientDto
{
    public int PaymentMethodId { get; set; }

    public string PaymentMethodName { get; set; } = string.Empty;

    public decimal CollectedAmount { get; set; }

    public decimal FeeAmount { get; set; }

    public decimal KnownFeeAmount { get; set; }

    public decimal UnknownFeeAmount { get; set; }

    public decimal FeeCoveragePercent { get; set; }
}

public sealed class CategoryBreakdownClientDto
{
    public int CategoryId { get; set; }

    public string CategoryName { get; set; } = string.Empty;

    public decimal Revenue { get; set; }

    public decimal Cogs { get; set; }

    public decimal GrossProfit { get; set; }

    public decimal GrossMarginPercent { get; set; }

    public decimal ContributionPercent { get; set; }
}

public sealed class ProductBreakdownClientDto
{
    public int ProductId { get; set; }

    public string ProductName { get; set; } = string.Empty;

    public int? CategoryId { get; set; }

    public string? CategoryName { get; set; }

    public decimal QuantitySold { get; set; }

    public decimal Revenue { get; set; }

    public decimal Cogs { get; set; }

    public decimal GrossProfit { get; set; }

    public decimal GrossMarginPercent { get; set; }
}

public sealed class SellerBreakdownClientDto
{
    public int? CashierId { get; set; }

    public string CashierName { get; set; } = string.Empty;

    public decimal NetSales { get; set; }

    public decimal GrossProfit { get; set; }
}

public sealed class ProfitabilityAlertClientDto
{
    public string Code { get; set; } = string.Empty;

    public ProfitabilityAlertSeverityClient Severity { get; set; }

    public string Message { get; set; } = string.Empty;

    public decimal CurrentValue { get; set; }

    public decimal PreviousValue { get; set; }

    public decimal Delta { get; set; }
}

/// <summary>F4-WU1: archivo binario devuelto por el endpoint server-side de exportación.</summary>
public sealed record ProfitabilityClientExport(
    byte[] Content,
    string FileName,
    string ContentType);

/// <summary>
/// F4-WU1: regla de comisión administrable. Los cobros históricos conservan su snapshot; esta
/// vista es únicamente la configuración vigente.
/// </summary>
public sealed class PaymentMethodFeeRuleClientDto : INotifyPropertyChanged
{
    private bool _isActive = true;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int Id { get; set; }

    public int PaymentMethodId { get; set; }

    public string PaymentMethodName { get; set; } = string.Empty;

    public PaymentMethodFeeTypeClient FeeType { get; set; }

    public decimal? Percentage { get; set; }

    public decimal? FixedAmount { get; set; }

    public string CurrencyCode { get; set; } = string.Empty;

    public DateTime EffectiveFromUtc { get; set; }

    public DateTime? EffectiveToUtc { get; set; }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value)
            {
                return;
            }

            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusDisplay)));
        }
    }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public string FeeTypeDisplay => FeeType switch
    {
        PaymentMethodFeeTypeClient.Fixed => "Fijo",
        PaymentMethodFeeTypeClient.PercentagePlusFixed => "Porcentaje + Fijo",
        _ => "Porcentaje"
    };

    public string FeeValueDisplay => FeeType switch
    {
        PaymentMethodFeeTypeClient.Fixed => FormatFixed(FixedAmount ?? 0m),
        PaymentMethodFeeTypeClient.PercentagePlusFixed =>
            $"{FormatPercentage(Percentage ?? 0m)} + {FormatFixed(FixedAmount ?? 0m)}",
        _ => FormatPercentage(Percentage ?? 0m)
    };

    public string EffectiveFromDisplay => EffectiveFromUtc.ToString("dd/MM/yyyy");

    public string EffectiveToDisplay => EffectiveToUtc.HasValue
        ? EffectiveToUtc.Value.ToString("dd/MM/yyyy")
        : "—";

    public string StatusDisplay => IsActive ? "Activa" : "Inactiva";

    private static string FormatPercentage(decimal value)
    {
        var formatted = CurrencyDisplay.Number(value, 4).TrimEnd('0').TrimEnd('.', ',');
        return $"{formatted} %";
    }

    private string FormatFixed(decimal value)
    {
        var symbol = CurrencyDisplay.SymbolFor(CurrencyCode);
        var formatted = CurrencyDisplay.Number(value, 2);
        return string.IsNullOrWhiteSpace(symbol) ? formatted : $"{symbol} {formatted}";
    }
}

public sealed class CreatePaymentMethodFeeRuleClientRequest
{
    public int PaymentMethodId { get; set; }

    public PaymentMethodFeeTypeClient FeeType { get; set; }

    public decimal? Percentage { get; set; }

    public decimal? FixedAmount { get; set; }

    public string CurrencyCode { get; set; } = string.Empty;

    public DateTime EffectiveFromUtc { get; set; }

    public DateTime? EffectiveToUtc { get; set; }

    public bool IsActive { get; set; } = true;
}

public sealed class UpdatePaymentMethodFeeRuleClientRequest
{
    public PaymentMethodFeeTypeClient FeeType { get; set; }

    public decimal? Percentage { get; set; }

    public decimal? FixedAmount { get; set; }

    public string CurrencyCode { get; set; } = string.Empty;

    public DateTime EffectiveFromUtc { get; set; }

    public DateTime? EffectiveToUtc { get; set; }

    public bool IsActive { get; set; } = true;
}
