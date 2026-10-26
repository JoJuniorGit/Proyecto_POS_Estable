using System;

namespace Core.DTOs;

/// <summary>8.211-T2a (S4): payload de carga manual de una tasa por par y fecha.</summary>
public sealed class ManualCurrencyRateRequest
{
    public string FromCurrencyCode { get; set; } = string.Empty;

    public string ToCurrencyCode { get; set; } = string.Empty;

    public DateOnly? RateDate { get; set; }

    public decimal Rate { get; set; }
}

/// <summary>8.211-T2a (S4): proyección de lectura de las tasas manuales persistidas.</summary>
public sealed class ManualCurrencyRateDto
{
    public string FromCurrencyCode { get; set; } = string.Empty;

    public string ToCurrencyCode { get; set; } = string.Empty;

    public decimal Rate { get; set; }

    public DateOnly RateDate { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public int? UpdatedByUserId { get; set; }
}
