using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Sales.Module.Entities;

public enum CashDrawerStatus
{
    Open,
    Closed
}

public class CashDrawerSession
{
    public int Id { get; set; }

    public DateTime OpenedAt { get; set; } = DateTime.UtcNow;
    [NotMapped] public DateTime OpenedAtLocal { get; set; }

    public DateTime? ClosedAt { get; set; }
    [NotMapped] public DateTime? ClosedAtLocal { get; set; }

    public CashDrawerStatus Status { get; set; } = CashDrawerStatus.Open;

    // The starting cash when the register opens (in Bs.S)
    [Column(TypeName = "decimal(18,2)")]
    public decimal OpeningBalanceLocal { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal OpeningExchangeRate { get; set; }

    // Optional: Used if the closing count differs from the expected system count
    [Column(TypeName = "decimal(18,2)")]
    public decimal? ClosingBalanceLocal { get; set; }

    [Column(TypeName = "decimal(18,2)")]
    public decimal? ClosingExchangeRate { get; set; }

    public List<CashTransaction> Transactions { get; set; } = new();
}
