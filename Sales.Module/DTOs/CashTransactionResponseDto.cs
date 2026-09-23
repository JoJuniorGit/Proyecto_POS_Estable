using Sales.Module.Entities;

namespace Sales.Module.DTOs;

public sealed record CashTransactionResponseDto
{
    public int Id { get; init; }
    public int SessionId { get; init; }
    public DateTime TransactionTime { get; init; }
    public DateTime TransactionTimeLocal { get; init; }
    public CashTransactionType Type { get; init; }
    public CashTransactionSource Source { get; init; }
    public decimal AmountUsd { get; init; }
    public decimal ExchangeRate { get; init; }
    public decimal AmountLocal { get; init; }
    public string Description { get; init; } = string.Empty;
    public int? ReferenceId { get; init; }
    public int? SaleId { get; init; }
    public int? InvoiceNumber { get; init; }
    public bool IsPhysicalCash { get; init; } = true;
    public int? PaymentMethodId { get; init; }
}
