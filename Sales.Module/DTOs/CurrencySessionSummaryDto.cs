namespace Sales.Module.DTOs;

/// <summary>
/// S1/S2 (Paso 14): resumen físico de la sesión para una moneda activa. Todos los montos están
/// expresados en la moneda nominal de la fila; nunca se convierten a otra divisa.
/// </summary>
public sealed record CurrencySessionSummaryDto
{
    public string CurrencyCode { get; init; } = string.Empty;
    public string CurrencySymbol { get; init; } = string.Empty;
    public decimal OpeningBalance { get; init; }
    public decimal TotalPhysicalInflows { get; init; }
    public decimal TotalPhysicalOutflows { get; init; }
    public decimal ExpectedCashBalance { get; init; }
    public bool IsLocal { get; init; }
}
