using System.ComponentModel.DataAnnotations;
using Core.Entities;

namespace Core.DTOs;

public class CreateProductDto
{
    [Required(ErrorMessage = "El nombre del producto es obligatorio.")]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public string? SKU { get; set; }

    [MaxLength(1000)]
    public string? Description { get; set; }

    [Range(0, 1000000)]
    public decimal PriceUSD { get; set; }

    [Range(0, 1000000)]
    public decimal PriceRetailUSD { get; set; }

    [Range(0, 1000000)]
    public decimal PriceBsS { get; set; }

    [Range(0, 1000000)]
    public decimal PriceWholesaleUSD { get; set; }

    [Range(0, 1000000)]
    public decimal CostPriceUSD { get; set; }

    [Range(0, 10000)]
    public decimal ProfitMarginRetail { get; set; }

    [Range(0, 10000)]
    public decimal ProfitMarginWholesale { get; set; }

    public decimal MinWholesaleQuantity { get; set; } = 6.000m;
    public bool HasWholesale { get; set; }
    public bool IsFractional { get; set; }
    public UnitOfMeasureType UnitOfMeasure { get; set; } = UnitOfMeasureType.Und;
    public decimal LowStockThreshold { get; set; }

    [Range(0, 1000000)]
    public decimal StockQuantity { get; set; }
    public bool IsCashAdvance { get; set; }
    public bool IsActive { get; set; } = true;

    public int? ParentProductId { get; set; }
    public bool IsGroupHeader { get; set; }
    public bool IsStockShared { get; set; }
    public bool HasIndependentPricing { get; set; }
    public decimal ConversionFactor { get; set; } = 1.0000m;
    public string? GroupKey { get; set; }

    // 8.179-T1 (S3): categorías a asignar. null o vacío = producto sin categorías.
    public IReadOnlyList<int>? CategoryIds { get; set; }

    // 8.184-T2 (S5): reglas de redondeo por artículo del modal de producto. null = hereda la
    // política global de Pricing (los enums viajan como enteros, convención del API).
    public RoundingRuleType? RoundingRuleUsd { get; set; }
    public RoundingRuleType? RoundingRuleCashBs { get; set; }
}

public class UpdateProductDto
{
    public int Id { get; set; }

    [Required(ErrorMessage = "El nombre del producto es obligatorio.")]
    [MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    public string? SKU { get; set; }

    [MaxLength(1000)]
    public string? Description { get; set; }

    [Range(0, 1000000)]
    public decimal PriceUSD { get; set; }

    [Range(0, 1000000)]
    public decimal PriceRetailUSD { get; set; }

    [Range(0, 1000000)]
    public decimal PriceBsS { get; set; }

    [Range(0, 1000000)]
    public decimal PriceWholesaleUSD { get; set; }

    [Range(0, 1000000)]
    public decimal CostPriceUSD { get; set; }

    [Range(0, 10000)]
    public decimal ProfitMarginRetail { get; set; }

    [Range(0, 10000)]
    public decimal ProfitMarginWholesale { get; set; }

    public decimal MinWholesaleQuantity { get; set; } = 6.000m;
    public bool HasWholesale { get; set; }
    public bool IsFractional { get; set; }
    public UnitOfMeasureType UnitOfMeasure { get; set; } = UnitOfMeasureType.Und;
    public decimal LowStockThreshold { get; set; }
    public bool IsCashAdvance { get; set; }
    public bool IsActive { get; set; }

    public int? ParentProductId { get; set; }
    public bool IsGroupHeader { get; set; }
    public bool IsStockShared { get; set; }
    public bool HasIndependentPricing { get; set; }
    public decimal ConversionFactor { get; set; } = 1.0000m;
    public string? GroupKey { get; set; }

    // 8.179-T1 (S3): semántica de reemplazo completo — null = no tocar las asignaciones actuales;
    // lista vacía = quitar todas; lista con IDs = reemplazar el set por esa lista validada.
    public IReadOnlyList<int>? CategoryIds { get; set; }

    // 8.184-T2 (S5): reemplazo completo (molde del resto del DTO) — null = heredar la política
    // global, es decir, "Heredar global" del selector limpia el override previo del artículo.
    public RoundingRuleType? RoundingRuleUsd { get; set; }
    public RoundingRuleType? RoundingRuleCashBs { get; set; }
}

public class StatusUpdateDto
{
    public bool IsActive { get; set; }
    public bool IsDeleted { get; set; }
}

// 8.181-T1 (S1-S4): contrato de la edición masiva de precios del catálogo. Preview y apply
// comparten estos tipos; Field/Mode viajan como enteros (el API no registra
// JsonStringEnumConverter: convención vigente de los DTOs del repo).
public enum BulkPriceField
{
    RetailPrice = 0,
    WholesalePrice = 1,
    RetailMargin = 2,
    WholesaleMargin = 3,
    CostPrice = 4
}

public enum BulkPriceMode
{
    SetFixed = 0,
    IncreasePercent = 1,
    DecreasePercent = 2
}

// 8.182-T1 (S1): reglas de redondeo comercial/psicológico de la edición masiva. Granularidad por
// sub-opción (entero / paso 5 / paso 10) para no agregar campos extra de configuración al DTO.
// ExactMath (default) conserva el comportamiento 8.181 intacto.
public enum RoundingRuleType
{
    ExactMath = 0,
    UsdPsychological = 1,
    VesCashFriendlyInteger = 2,
    VesCashFriendlyStep5 = 3,
    VesCashFriendlyStep10 = 4
}

public class BulkPriceOperationDto
{
    public BulkPriceField Field { get; set; }
    public BulkPriceMode Mode { get; set; }

    // SetFixed: valor absoluto. IncreasePercent/DecreasePercent: en precio/costo el valor es un
    // porcentaje multiplicativo sobre el valor actual; en los márgenes (RetailMargin/WholesaleMargin)
    // se interpreta SIEMPRE como puntos porcentuales aditivos (actual ± Δ; p. ej. 10 + 10 = 20) y las
    // reducciones se clipean al piso del canal o a 0 (nunca quedan negativas).
    public decimal Value { get; set; }
}

// 8.181-T1 (S1): la selección por filtro replica la semántica de GetProductsPagedAsync (los mismos
// parámetros del listado del catálogo) para que "seleccionar todos los filtrados" coincida con lo listado.
public class BulkPriceSelectionFilterDto
{
    public string? SearchText { get; set; }
    public string? StatusFilter { get; set; }
    public int? CategoryId { get; set; }
}

public class BulkPriceUpdateRequestDto
{
    // Modo 1: selección explícita (1..1000 IDs).
    public List<int>? ProductIds { get; set; }

    // Modo 2: todos los productos que matchean el filtro (Filter nulo = catálogo completo).
    public BulkPriceSelectionFilterDto? Filter { get; set; }
    public bool SelectAllFiltered { get; set; }

    public List<BulkPriceOperationDto> Operations { get; set; } = new();

    // 8.182-T1 (S5) + Spec V0.15: redondeo USD por canal (default ExactMath = comportamiento 8.181).
    // La proyección Bs.S de efectivo vive en CashBsRoundingRule (capa independiente). Pisos de margen
    // por lote (supuesto v1: sin setting global): se evalúan con EnforceMinimumMarginFloor activo,
    // piso > 0 y costo > 0.
    public RoundingRuleType RetailRoundingRule { get; set; } = RoundingRuleType.ExactMath;
    public RoundingRuleType WholesaleRoundingRule { get; set; } = RoundingRuleType.ExactMath;

    // Spec V0.15: capa de proyección Bs.S en caliente (columna BS.S EFECTIVO (PROY.)) con las reglas
    // VesCashFriendly*; ExactMath (default) = sin proyección. NUNCA se persiste. Un caller viejo que
    // envíe una regla VES en el canal ya no proyecta: esa vía quedó defensiva en el motor.
    public RoundingRuleType CashBsRoundingRule { get; set; } = RoundingRuleType.ExactMath;

    // Spec V0.15 (persistencia de política): baselines globales del lote para comparar la herencia
    // (Product.RoundingRuleUsd/RoundingRuleCashBs null = hereda global) sin materializar la política
    // cuando la efectiva ya coincide con la wanted. El VM los completa con las políticas sembradas.
    public RoundingRuleType GlobalUsdRoundingRule { get; set; } = RoundingRuleType.ExactMath;
    public RoundingRuleType GlobalCashBsRoundingRule { get; set; } = RoundingRuleType.ExactMath;

    public bool EnforceMinimumMarginFloor { get; set; }
    public decimal? RetailMarginFloorPct { get; set; }
    public decimal? WholesaleMarginFloorPct { get; set; }
}

public class BulkPriceFieldChangeDto
{
    public BulkPriceField Field { get; set; }

    // Para RetailPrice estos valores son el precio efectivo (PriceRetailUSD con respaldo en PriceUSD).
    public decimal BeforeValue { get; set; }
    public decimal AfterValue { get; set; }

    // Bs.S canónico (tag de catálogo) antes/después; se informa solo en cambios del precio al detal.
    // 8.182-T1: con una regla VES activa sobre el canal al mayor (sin columna Bs.S propia en la
    // entidad) estos campos transportan el Bs.S equivalente informativo de ese canal.
    // 8.183 (R1): en el detal con regla VES el valor informado es SIEMPRE el canónico almacenado
    // (nunca el amigable): la proyección de efectivo viaja en CashFriendlyAfterBsS.
    public decimal? BeforeBsS { get; set; }
    public decimal? AfterBsS { get; set; }

    // 8.182-T1 (S5): valor matemático previo al redondeo y delta con signo (redondeado − matemático)
    // en la unidad de la regla del canal: USD para UsdPsychological; Bs.S para VesCashFriendly*.
    // 8.183 (R1): para VES la traza describe la PROYECCIÓN en caliente (nunca persistida); null
    // cuando la regla del canal no generó traza para este cambio (ExactMath o campo no alcanzado).
    public decimal? PreRoundingValue { get; set; }
    public decimal? RoundingDelta { get; set; }

    // 8.183 (R1): proyección en caliente del precio Bs.S con la regla de efectivo del canal y la tasa
    // del día (friendly = Round(usd × tasa, regla)); NUNCA se persiste — el PriceBsS almacenado es el
    // canónico 8.104 (techo). null para reglas USD/ExactMath o sin tasa/valor convertible.
    public decimal? CashFriendlyAfterBsS { get; set; }
}

public class BulkPricePreviewRowDto
{
    public int ProductId { get; set; }
    public string SKU { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsGroupHeader { get; set; }
    public bool HasIndependentPricing { get; set; }

    // Motivo legible de la omisión (vacío/null si la fila se actualiza). IsError distingue las
    // omisiones de negocio (p. ej. grupo con precios independientes) de los errores que bloquean
    // el apply completo con 400 (resultado negativo, mayor>detal, cap excedido).
    public string? SkipReason { get; set; }
    public bool IsError { get; set; }

    // Spec V0.15: nota de política cuando la configuración del lote no puede materializarse por
    // artículo (p. ej. reglas USD distintas por canal: la columna es única por producto). null = sin nota.
    public string? PolicyNote { get; set; }

    // 8.182-T1 (S5): margen efectivo (precio final − costo) / costo × 100 del canal, calculado sobre
    // el precio FINAL; informado solo cuando el precio USD del canal cambió y el costo es > 0.
    // 8.183 (R3): es SOLO informativo — el margen persistido es siempre el objetivo/operado (el
    // redondeo jamás lo sobrescribe; el efectivo se deriva del precio final para mostrarlo).
    public decimal? EffectiveRetailMarginPct { get; set; }
    public decimal? EffectiveWholesaleMarginPct { get; set; }

    public List<BulkPriceFieldChangeDto> Changes { get; set; } = new();
}

public class BulkPriceUpdateResultDto
{
    // Tasa del día usada para el Bs.S canónico (0 = sin tasa configurada).
    public decimal ExchangeRate { get; set; }
    public int SelectedCount { get; set; }
    public int UpdatedCount { get; set; }
    public int SkippedCount { get; set; }
    public List<BulkPricePreviewRowDto> Rows { get; set; } = new();
}
