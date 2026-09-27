namespace Sales.Module.DTOs;

public sealed record ClosureDetailResponseDto(
    int Id,
    int DailyClosureId,
    int PaymentMethodId,
    string PaymentMethodName,
    decimal ExpectedAmountBsS,
    decimal ActualAmountBsS,
    decimal DifferenceBsS);
