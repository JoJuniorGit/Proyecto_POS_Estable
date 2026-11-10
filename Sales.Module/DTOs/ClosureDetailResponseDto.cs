using System.Text.Json.Serialization;

namespace Sales.Module.DTOs;

public sealed record ClosureDetailResponseDto(
    int Id,
    int DailyClosureId,
    int PaymentMethodId,
    string PaymentMethodName,
    decimal ExpectedAmountBsS,
    decimal ActualAmountBsS,
    decimal DifferenceBsS,
    // 8.211-T3b2 (S8/S10): moneda persistida del método de pago. Es aditiva y opcional para no
    // romper a los consumidores previos de 7 argumentos. Null cuando la navegación no fue cargada
    // (fila legada): los comprobantes y reportes caen a la regla histórica por nombre. Se omite del
    // JSON en ese caso para preservar exactamente el contrato dorado de los cierres previos.
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PaymentMethodCurrency = null);
