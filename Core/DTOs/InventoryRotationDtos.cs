namespace Core.DTOs;

/// <summary>
/// Paso 15 (S2): bandas de salud del inventario. Los valores viajan como texto estable para que
/// WPF/Web no dependan del orden numerico de un enum compartido.
/// </summary>
public static class InventoryRotationClass
{
    public const string High = "High";
    public const string Medium = "Medium";
    public const string Low = "Low";
    public const string DeadStock = "DeadStock";

    public static bool IsValid(string? value) =>
        value is High or Medium or Low or DeadStock;
}

/// <summary>
/// Paso 15 (S1-S4): filtros del reporte on-demand. La ventana de ventas y el umbral de stock
/// muerto son independientes: la primera alimenta velocidad/dias promedio y el segundo decide
/// cuando un producto deja de considerarse con rotacion.
/// </summary>
public sealed class InventoryRotationFilterDto
{
    public int SalesWindowDays { get; set; } = 90;
    public int DeadStockDays { get; set; } = 60;
    public int? CategoryId { get; set; }
    public bool IncludeCategorySubtree { get; set; } = true;
    public string? SearchText { get; set; }
    public string? RotationClass { get; set; }
    public string SortBy { get; set; } = "immobilizedCapital";
    public bool Descending { get; set; } = true;
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}

/// <summary>
/// Paso 15 (S6): cifras ejecutivas sobre todo el universo filtrado, no solo la pagina visible.
/// </summary>
public sealed class InventoryRotationSummaryDto
{
    public int TotalProducts { get; init; }
    public int HighRotationCount { get; init; }
    public int MediumRotationCount { get; init; }
    public int LowRotationCount { get; init; }
    public int DeadStockCount { get; init; }
    public decimal HighRotationPercent { get; init; }
    public decimal MediumRotationPercent { get; init; }
    public decimal LowRotationPercent { get; init; }
    public decimal DeadStockPercent { get; init; }
    public decimal TotalInventoryValueUsd { get; init; }
    public decimal ImmobilizedCapitalUsd { get; init; }
    public decimal ImmobilizedCapitalPercent { get; init; }
}

/// <summary>
/// Paso 15 (S1-S3/S5): fila de producto con las metricas que alimentan la tabla y el puente al
/// Paso 9. <see cref="ProductId"/> es el identificador canonico que viaja a
/// <c>BulkUpdatePricesAsync</c>.
/// </summary>
public sealed class InventoryRotationProductDto
{
    public int ProductId { get; init; }
    public string SKU { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int? CategoryId { get; init; }
    public string CategoryName { get; init; } = string.Empty;
    public bool IsActive { get; init; }
    public decimal StockQuantity { get; init; }
    public decimal CostPriceUsd { get; init; }
    public decimal InventoryValueUsd { get; init; }
    public decimal UnitsSold { get; init; }
    public decimal SalesRevenueUsd { get; init; }
    public DateTime? LastSaleDateUtc { get; init; }
    public int DaysSinceLastSale { get; init; }
    public decimal SalesVelocityPerDay { get; init; }
    public decimal? AverageInventoryDays { get; init; }
    public string RotationClass { get; init; } = InventoryRotationClass.Low;
    public bool IsDeadStock => RotationClass == InventoryRotationClass.DeadStock;
    public string RotationClassLabel => RotationClass switch
    {
        InventoryRotationClass.High => "Alta",
        InventoryRotationClass.Medium => "Media",
        InventoryRotationClass.Low => "Baja",
        InventoryRotationClass.DeadStock => "Muerto",
        _ => RotationClass
    };
}

/// <summary>
/// Paso 15 (S1/S6): agregado por categoria (primera categoria del producto, ordenada por Id).
/// </summary>
public sealed class InventoryRotationCategoryDto
{
    public int? CategoryId { get; init; }
    public string CategoryName { get; init; } = string.Empty;
    public int ProductCount { get; init; }
    public decimal StockQuantity { get; init; }
    public decimal InventoryValueUsd { get; init; }
    public decimal UnitsSold { get; init; }
    public decimal SalesRevenueUsd { get; init; }
    public decimal? AverageInventoryDays { get; init; }
    public int DeadStockCount { get; init; }
    public decimal ImmobilizedCapitalUsd { get; init; }
    public string RotationClass { get; init; } = InventoryRotationClass.Low;
    public string RotationClassLabel => RotationClass switch
    {
        InventoryRotationClass.High => "Alta",
        InventoryRotationClass.Medium => "Media",
        InventoryRotationClass.Low => "Baja",
        InventoryRotationClass.DeadStock => "Muerto",
        _ => RotationClass
    };
}

/// <summary>
/// Paso 15 (S6): contrato completo del informe ejecutivo. <see cref="Products"/> es una pagina;
/// <see cref="Summary"/> y <see cref="Categories"/> describen el universo filtrado completo.
/// </summary>
public sealed class InventoryRotationReportDto
{
    public DateTime GeneratedAtUtc { get; init; }
    public DateTime FromUtc { get; init; }
    public DateTime ToUtc { get; init; }
    public int SalesWindowDays { get; init; }
    public int DeadStockDays { get; init; }
    public InventoryRotationSummaryDto Summary { get; init; } = new();
    public IReadOnlyList<InventoryRotationProductDto> Products { get; init; } = Array.Empty<InventoryRotationProductDto>();
    public IReadOnlyList<InventoryRotationCategoryDto> Categories { get; init; } = Array.Empty<InventoryRotationCategoryDto>();
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
    public int TotalPages { get; init; }
}

/// <summary>
/// Paso 15 (S5): seleccion pre-filtrada para el dialogo del Paso 9. Se corta en el maximo que
/// acepta <c>BulkUpdatePricesAsync</c> (1000) y declara el total real para que la UI avise.
/// </summary>
public sealed class DeadStockSelectionDto
{
    public IReadOnlyList<int> ProductIds { get; init; } = Array.Empty<int>();
    public int TotalCount { get; init; }
    public bool Truncated { get; init; }
}

/// <summary>
/// Paso 15 (S7): proyeccion minima de ventas por producto dentro de la ventana.
/// </summary>
public sealed record InventoryRotationSalesAggregateDto
{
    public int ProductId { get; init; }
    public decimal UnitsSold { get; init; }
    public decimal SalesRevenueUsd { get; init; }
}

/// <summary>
/// Paso 15 (S2): ultima venta valida del producto, sin limite de ventana.
/// </summary>
public sealed record InventoryRotationLastSaleDto
{
    public int ProductId { get; init; }
    public DateTime LastSaleDateUtc { get; init; }
}

/// <summary>
/// Paso 15 (S1-S3): snapshot de catalogo que Inventory.Module entrega a Sales.Module sin exponer
/// entidades EF ni acoplar los contextos.
/// </summary>
public sealed record InventoryRotationProductSnapshotDto
{
    public int ProductId { get; init; }
    public string SKU { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public int? CategoryId { get; init; }
    public string? CategoryName { get; init; }
    public bool IsActive { get; init; }
    public decimal StockQuantity { get; init; }
    public decimal CostPriceUsd { get; init; }
    public DateTime CreatedAtUtc { get; init; }
}
