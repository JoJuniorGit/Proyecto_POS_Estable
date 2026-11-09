using Sales.Module.Entities;
using System.Text.Json.Serialization;

namespace Sales.Module.DTOs;

public sealed record CashDrawerSessionResponseDto
{
    public int Id { get; init; }
    public DateTime OpenedAt { get; init; }
    public DateTime OpenedAtLocal { get; init; }
    public DateTime? ClosedAt { get; init; }
    public DateTime? ClosedAtLocal { get; init; }
    public CashDrawerStatus Status { get; init; }
    public decimal OpeningBalanceLocal { get; init; }
    public decimal OpeningBalanceUsd { get; init; }
    public decimal OpeningExchangeRate { get; init; }
    public decimal? ClosingBalanceLocal { get; init; }
    public decimal? ClosingBalanceUsd { get; init; }
    public decimal? ClosingExchangeRate { get; init; }
    public IReadOnlyList<CashTransactionResponseDto> Transactions { get; init; } = [];

    /// <summary>
    /// 8.212-WU2: per-method physical balances. It stays null (and is omitted from JSON) for
    /// sessions created before WU1, preserving the legacy active-session payload byte-for-byte.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<PhysicalMethodBalanceDto>? PhysicalMethodBalances { get; init; }

    /// <summary>
    /// S1/S2: resumen físico por moneda activa (local primero). El cliente no debe recalcular
    /// conversiones ni atribuciones cuando este bloque está presente.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<CurrencySessionSummaryDto>? CurrencySummaries { get; init; }
}
