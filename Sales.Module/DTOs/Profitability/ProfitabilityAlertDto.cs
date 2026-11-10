namespace Sales.Module.DTOs.Profitability;

/// <summary>
/// Paso 14 (F2-S4): alerta de erosión de margen. <see cref="CurrentValue"/> y
/// <see cref="PreviousValue"/> quedan en la unidad del indicador que la genera.
/// </summary>
public sealed record ProfitabilityAlertDto
{
    public string Code { get; init; } = string.Empty;

    public ProfitabilityAlertSeverity Severity { get; init; }

    public string Message { get; init; } = string.Empty;

    public decimal CurrentValue { get; init; }

    public decimal PreviousValue { get; init; }

    public decimal Delta { get; init; }
}
