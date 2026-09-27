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
    IReadOnlyList<ClosureDetailResponseDto> Details);
