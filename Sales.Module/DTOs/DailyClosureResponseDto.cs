using System.Text.Json.Serialization;

namespace Sales.Module.DTOs;

public sealed record DailyClosureResponseDto(
    int Id,
    DateTime ClosureDate,
    string? UserId,
    decimal ExchangeRate,
    decimal TotalExpectedBsS,
    decimal TotalActualBsS,
    decimal TotalDifferenceBsS,
    string? Observation,
    IReadOnlyList<ClosureDetailResponseDto> Details,
    // T2b (S2): bloque OPEX aditivo. Se omite del JSON cuando el caller mapea sin resumen
    // (p. ej. contratos legacy en tests); el servicio de cierre siempre lo informa.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    PettyCashSummaryDto? PettyCashSummary = null);
