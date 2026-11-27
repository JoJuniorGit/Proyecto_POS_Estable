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
    PettyCashSummaryDto? PettyCashSummary = null,
    // Residual 8.187-8.188: arqueo USD aditivo. Null (cierre sin conteo USD o respuesta blind)
    // se omite del JSON para no alterar el contrato dorado de los cierres previos.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    decimal? ExpectedClosingBalanceUsd = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    decimal? ActualClosingBalanceUsd = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    decimal? DifferenceUsd = null);
