namespace Sales.Module.DTOs;

public class OpenSessionRequest
{
    public decimal OpeningBalanceLocal { get; set; }

    /// <summary>
    /// Fondo físico inicial de la gaveta USD. Opcional para conservar el contrato previo;
    /// ausente equivale a 0. El rollover automático lo transfiere desde el cierre anterior.
    /// </summary>
    public decimal OpeningBalanceUsd { get; set; }

    public decimal CurrentExchangeRate { get; set; }

    /// <summary>
    /// 8.212-WU1: optional per-method opening breakdown. When omitted, a legacy open auto-assigns
    /// the local/USD opening only if exactly one physical method exists for that currency.
    /// </summary>
    public List<OpenSessionMethodBalanceRequest>? MethodBalances { get; set; }
}

public sealed class OpenSessionMethodBalanceRequest
{
    public int PaymentMethodId { get; set; }
    public decimal OpeningAmount { get; set; }
}

public class CloseSessionRequest
{
    public decimal ActualClosingBalanceLocal { get; set; }
    public decimal CurrentExchangeRate { get; set; }

    /// <summary>
    /// T4 (Paso 13 / 8.188, S2): conteo físico USD opcional del cierre. Ausente/null conserva el
    /// comportamiento previo (la sesión no persiste <c>ClosingBalanceUsd</c>).
    /// </summary>
    public decimal? ActualClosingBalanceUsd { get; set; }
}

public class AddTransactionRequest
{
    public int SessionId { get; set; }
    public decimal AmountLocal { get; set; }
    public Sales.Module.Entities.CashTransactionType Type { get; set; }
    public Sales.Module.Entities.CashTransactionSource Source { get; set; }
    public decimal ExchangeRate { get; set; }
    public string Description { get; set; } = string.Empty;
}

public class CashAdvanceRequest
{
    public int SessionId { get; set; }
    public decimal RequestedAmountLocal { get; set; }
    public int PaymentMethodId { get; set; }
    public string PaymentMethodName { get; set; } = string.Empty;
    public bool IsTransfer { get; set; }
    public decimal ExchangeRate { get; set; }
}

/// <summary>
/// T4 (Paso 13 / 8.188, S2): saldos físicos teóricos del turno, segregados por la moneda canónica
/// del método de pago. Solo los consume la UI de supervisión (arqueo ciego).
/// </summary>
public sealed record PettyCashPhysicalBalancesResponseDto(decimal BsS, decimal Usd);
