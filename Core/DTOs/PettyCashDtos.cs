using System;

namespace Core.DTOs;

/// <summary>
/// T2a (Paso 13 / 8.188, S4): señal de dominio del egreso de caja chica cuando la jerarquía exige
/// autorización y el caller no la presentó. El controlador la traduce al contrato 403 con
/// authorizationAction=PettyCashExpense y el contexto exacto que el hub debe reproducir; con un
/// token vigente el controlador consume y reintenta habilitando SOLO esta excepción.
/// Los enums de moneda/categoría viajan como enteros (Core no referencia Sales.Module): los
/// valores son los ids persistidos de <c>PettyCashCurrency</c> / <c>PettyCashCategory</c>.
/// </summary>
public sealed class PettyCashAuthorizationRequiredException : InvalidOperationException
{
    public int SessionId { get; }

    /// <summary>Id persistido de PettyCashCurrency (0 = Bs.S, 1 = USD).</summary>
    public int Currency { get; }

    /// <summary>Monto nominal/contravalor Bs.S del gasto (snapshot de la operación).</summary>
    public decimal AmountBsS { get; }

    /// <summary>Contravalor USD del gasto (snapshot de la operación).</summary>
    public decimal AmountUsd { get; }

    /// <summary>Id persistido de PettyCashCategory.</summary>
    public int Category { get; }

    public PettyCashAuthorizationRequiredException(
        int sessionId,
        int currency,
        decimal amountBsS,
        decimal amountUsd,
        int category,
        string message)
        : base(message)
    {
        SessionId = sessionId;
        Currency = currency;
        AmountBsS = amountBsS;
        AmountUsd = amountUsd;
        Category = category;
    }
}
