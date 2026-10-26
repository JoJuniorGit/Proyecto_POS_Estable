using System;

namespace Core.Entities;

/// <summary>
/// 8.211-T2a (S4): tasa manual diaria de un par de monedas distinto de USD/VES. USD/VES y su
/// inverso siguen resolviéndose desde <see cref="ExchangeRateHistory"/> (BCV); esta tabla solo
/// conserva la carga manual por fecha exacta, sin fallback. No hay FK a usuarios: el
/// <see cref="UpdatedByUserId"/> es auditoría histórica y no debe romper si el usuario se elimina.
/// </summary>
public class CurrencyPairRate
{
    public int Id { get; set; }

    public DateOnly RateDate { get; set; }

    public string FromCurrencyCode { get; set; } = string.Empty;

    public string ToCurrencyCode { get; set; } = string.Empty;

    public decimal Rate { get; set; }

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public int? UpdatedByUserId { get; set; }
}
