using Sales.Module.Entities;

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
    public decimal OpeningExchangeRate { get; init; }
    public decimal? ClosingBalanceLocal { get; init; }
    public decimal? ClosingExchangeRate { get; init; }
    public IReadOnlyList<CashTransactionResponseDto> Transactions { get; init; } = [];
}
