namespace Sales.Module.DTOs.Profitability;

/// <summary>
/// Paso 14 (F2-S4): desgloses del período. Cada colección es independiente y puede quedar vacía
/// cuando el filtro no aporta datos para ese eje.
/// </summary>
public sealed record ProfitabilityBreakdownDto
{
    public IReadOnlyList<ProfitabilitySeriesPointDto> Series { get; init; } = [];

    public IReadOnlyList<PaymentMethodBreakdownDto> PaymentMethods { get; init; } = [];

    public IReadOnlyList<CategoryBreakdownDto> Categories { get; init; } = [];

    public IReadOnlyList<ProductBreakdownDto> Products { get; init; } = [];

    public IReadOnlyList<SellerBreakdownDto> Sellers { get; init; } = [];
}

/// <summary>
/// Paso 14 (F2-S4): punto de la serie temporal. <see cref="PeriodStartUtc"/> es el inicio del
/// intervalo agrupado, ya convertido desde la Zona Horaria Global.
/// </summary>
public sealed record ProfitabilitySeriesPointDto
{
    public DateTime PeriodStartUtc { get; init; }

    public string Label { get; init; } = string.Empty;

    public decimal AccruedNetProfit { get; init; }

    public decimal RealizedNetProfit { get; init; }

    public decimal AccruedNetSales { get; init; }

    public decimal RealizedCollectedRevenue { get; init; }
}

/// <summary>
/// Paso 14 (F2-S4/S7): desglose por método de pago con la comisión del adquirente y su cobertura.
/// </summary>
public sealed record PaymentMethodBreakdownDto
{
    public int PaymentMethodId { get; init; }

    public string PaymentMethodName { get; init; } = string.Empty;

    public decimal CollectedAmount { get; init; }

    public decimal FeeAmount { get; init; }

    public decimal KnownFeeAmount { get; init; }

    public decimal UnknownFeeAmount { get; init; }

    public decimal FeeCoveragePercent { get; init; }
}

/// <summary>
/// Paso 14 (F2-S4): contribución monetaria y margen por categoría.
/// </summary>
public sealed record CategoryBreakdownDto
{
    public int CategoryId { get; init; }

    public string CategoryName { get; init; } = string.Empty;

    public decimal Revenue { get; init; }

    public decimal Cogs { get; init; }

    public decimal GrossProfit { get; init; }

    public decimal GrossMarginPercent { get; init; }

    public decimal ContributionPercent { get; init; }
}

/// <summary>
/// Paso 14 (F2-S4): ranking de productos por contribución monetaria y margen.
/// </summary>
public sealed record ProductBreakdownDto
{
    public int ProductId { get; init; }

    public string ProductName { get; init; } = string.Empty;

    public decimal QuantitySold { get; init; }

    public decimal Revenue { get; init; }

    public decimal Cogs { get; init; }

    public decimal GrossProfit { get; init; }

    public decimal GrossMarginPercent { get; init; }
}

/// <summary>
/// Paso 14 (F2-S4): ventas netas y utilidad bruta atribuidas al cajero/vendedor.
/// </summary>
public sealed record SellerBreakdownDto
{
    public int CashierId { get; init; }

    public string CashierName { get; init; } = string.Empty;

    public decimal NetSales { get; init; }

    public decimal GrossProfit { get; init; }
}
