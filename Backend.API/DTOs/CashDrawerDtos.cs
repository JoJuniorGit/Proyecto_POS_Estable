using System;
using Sales.Module.Entities;

namespace Backend.API.DTOs;

public class CashTransactionDto
{
    public int Id { get; set; }
    public DateTime TransactionTimeLocal { get; set; }
    public string Description { get; set; } = string.Empty;
    public int? InvoiceNumber { get; set; }
    public decimal AmountUsd { get; set; }
    public decimal AmountLocal { get; set; }
    public decimal ExchangeRate { get; set; }
    public CashTransactionType Type { get; set; }
    public CashTransactionSource Source { get; set; }
    public bool IsPhysicalCash { get; set; } = true;
    public int? PaymentMethodId { get; set; }
}