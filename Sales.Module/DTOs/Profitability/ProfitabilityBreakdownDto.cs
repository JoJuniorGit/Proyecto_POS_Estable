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

    /// <summary>
    /// Paso 14 (F3-S1): alertas de erosión de margen del período, incluidas también en la
    /// respuesta de <c>GET /api/profitability/breakdown</c>.
    /// </summary>
    public IReadOnlyList<ProfitabilityAlertDto> Alerts { get; init; } = [];
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

    /// <summary>Comisión neta del método en el período (incluye reversas S10 por anulación).</summary>
    public decimal FeeAmount { get; init; }

    /// <summary>
    /// Base de cobertura: monto cobrado por el método con regla de comisión conocida. No es el
    /// importe de la comisión; la comisión efectiva vive en <see cref="FeeAmount"/>.
    /// </summary>
    public decimal KnownFeeAmount { get; init; }

    /// <summary>
    /// Base de cobertura: monto cobrado por el método sin regla de comisión conocida. No es un
    /// importe de comisión desconocido; expresa qué porción de <see cref="CollectedAmount"/> no
    /// pudo asociarse a una regla vigente.
    /// </summary>
    public decimal UnknownFeeAmount { get; init; }

    /// <summary>Porcentaje de la base de cobro con regla de comisión conocida (0-100).</summary>
    public decimal FeeCoveragePercent { get; init; }
}

/// <summary>
/// Paso 14 (F2-S4): contribución monetaria y margen por categoría.
/// </summary>
public sealed record CategoryBreakdownDto
{
    /// <summary>Id de categoría; 0 identifica el grupo sintético "Sin categoría".</summary>
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

    /// <summary>Categoría de reporte del producto; null = sin categoría.</summary>
    public int? CategoryId { get; init; }

    public string? CategoryName { get; init; }

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
    /// <summary>Cajero de la venta; null = venta sin cajero asignado.</summary>
    public int? CashierId { get; init; }

    public string CashierName { get; init; } = string.Empty;

    public decimal NetSales { get; init; }

    public decimal GrossProfit { get; init; }
}
