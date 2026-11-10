namespace Core.DTOs;

/// <summary>
/// Paso 14 (F2-WU4, F2-S4): información mínima de catálogo para los desgloses de rentabilidad.
/// Un producto sin categoría llega con <see cref="CategoryId"/> y <see cref="CategoryName"/> en
/// null; el motor de reportes lo agrupa bajo "Sin categoría" sin inventar una categoría real.
/// </summary>
public sealed record ProfitabilityProductInfoDto
{
    public int ProductId { get; init; }

    public string ProductName { get; init; } = string.Empty;

    public int? CategoryId { get; init; }

    public string? CategoryName { get; init; }
}
