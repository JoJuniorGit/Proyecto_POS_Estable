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

public class BulkPriceOperationDto
{
    public BulkPriceField Field { get; set; }
    public BulkPriceMode Mode { get; set; }

    // SetFixed: valor absoluto. IncreasePercent/DecreasePercent: porcentaje aplicado al valor actual.
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
}

public class BulkPriceFieldChangeDto
{
    public BulkPriceField Field { get; set; }

    // Para RetailPrice estos valores son el precio efectivo (PriceRetailUSD con respaldo en PriceUSD).
    public decimal BeforeValue { get; set; }
    public decimal AfterValue { get; set; }

    // Bs.S canónico (tag de catálogo) antes/después; se informa solo en cambios del precio al detal.
    public decimal? BeforeBsS { get; set; }
    public decimal? AfterBsS { get; set; }
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
