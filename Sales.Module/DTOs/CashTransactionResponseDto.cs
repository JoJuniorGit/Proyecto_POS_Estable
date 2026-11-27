using Sales.Module.Entities;
using System.Text.Json.Serialization;

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

    /// <summary>Moneda fisica del movimiento (8.211-T3b3, S6); null en filas previas al backfill.</summary>
    // 8.211-T3b3 (S6): snapshot aditivo. El JSON omite el campo cuando es null para no alterar
    // el contrato dorado del arqueo legacy; las filas backfilleadas y nuevas siempre lo publican.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CurrencyCode { get; init; }
}
