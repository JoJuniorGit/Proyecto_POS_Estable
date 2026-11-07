using System;
using System.ComponentModel.DataAnnotations;

namespace Sales.Module.Entities;

/// <summary>
/// 8.212-WU1: physical cash drawer snapshot per payment method for one session. The row is
/// created when the session opens, keeps the method name/currency frozen for historical
/// reporting, and stores the opening/closing amounts for that method.
/// </summary>
public class CashDrawerSessionMethodBalance
{
    public int Id { get; set; }

    public int CashDrawerSessionId { get; set; }

    public int PaymentMethodId { get; set; }

    [Required]
    [MaxLength(150)]
    public string PaymentMethodName { get; set; } = string.Empty;

    [Required]
    [MaxLength(10)]
    public string CurrencyCode { get; set; } = string.Empty;

    public decimal OpeningAmount { get; set; }

    public decimal? ClosingAmount { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    public CashDrawerSession? Session { get; set; }

    public PaymentMethod? PaymentMethod { get; set; }
}
