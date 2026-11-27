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

    [Column(TypeName = "decimal(18,4)")]
    public decimal OpeningExchangeRate { get; set; }

    // Optional: Used if the closing count differs from the expected system count
    [Column(TypeName = "decimal(18,2)")]
    public decimal? ClosingBalanceLocal { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal? ClosingExchangeRate { get; set; }

    // 8.188-T1 (Paso 13, S2): gaveta dual — la apertura/cierre fisico en USD convive con el
    // Bs.S para deducir los egresos de caja chica de la gaveta de su propia moneda.
    // OpeningBalanceUsd NOT NULL DEFAULT 0; ClosingBalanceUsd nullable con DEFAULT 0 en la
    // migracion para filas existentes (la migracion nunca inventa un conteo de cierre).
    [Column(TypeName = "decimal(18,4)")]
    public decimal OpeningBalanceUsd { get; set; }

    [Column(TypeName = "decimal(18,4)")]
    public decimal? ClosingBalanceUsd { get; set; }

    public List<CashTransaction> Transactions { get; set; } = new();

    // 8.212-WU1: per-method physical opening/closing snapshot. Empty on legacy sessions.
    public List<CashDrawerSessionMethodBalance> MethodBalances { get; set; } = new();
}
