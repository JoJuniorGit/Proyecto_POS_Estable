using Sales.Module.DTOs;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Interfaces;

public class ExpectedTotalDto
{
    public int PaymentMethodId { get; set; }
    public string PaymentMethodName { get; set; } = string.Empty;
    public decimal ExpectedAmountBsS { get; set; }

    /// <summary>
    /// 8.211-T3b2 (S8): moneda persistida del método de pago, ya normalizada por
    /// <see cref="Sales.Module.PaymentMethodCurrencyResolver"/>. El servicio de cierre siempre la
    /// informa (persistida o fallback por nombre); vacía solo si un llamador construye el DTO por
    /// fuera del servicio.
    /// </summary>
    public string PaymentMethodCurrency { get; set; } = string.Empty;
}

public sealed record CloseShiftResult(
    int ClosureId,
    string CashierName,
    string CashierCedula,
    DateTime ClosedAt,
    decimal ExchangeRate,
    IReadOnlyList<ShiftReportDetailResult> Details,
    // Residual 8.187-8.188: arqueo USD del cierre. El controller anula esperado/diferencia para
    // roles no supervisores (blind 8.153); el contado se conserva tal como lo informo el usuario.
    decimal? ExpectedClosingBalanceUsd = null,
    decimal? ActualClosingBalanceUsd = null,
    decimal? DifferenceUsd = null);

public sealed record ShiftReportDetailResult(
    int PaymentMethodId,
    string PaymentMethodName,
    string Currency,
    decimal DeclaredAmount,
    decimal SystemAmount,
    decimal Difference,
    string Status);

/// <summary>
/// 8.207 (SRE-02a): capacidad opcional para que el cierre registre su idempotencia dentro de la
/// misma transacción Serializable que la mutación de negocio.
/// </summary>
public interface IAtomicIdempotentClosureService
{
    Task<CloseShiftResult> CreateClosureFromCommandAtomicAsync(
        CreateClosureCommand command,
        string? idempotencyKey,
        byte[]? payloadHash,
        string? requestPath,
        Func<CloseShiftResult, string>? responseBodyFactory,
        CancellationToken cancellationToken);
}

public interface IDailyClosureService
{
    Task<List<ExpectedTotalDto>> GetExpectedTotalsByPaymentMethodAsync(DateTime dateUtc, CancellationToken cancellationToken = default);
    Task<CloseShiftResult> CreateClosureFromCommandAsync(CreateClosureCommand command, CancellationToken cancellationToken);
    Task<DailyClosureResponseDto?> GetClosureAsync(int id, CancellationToken cancellationToken = default);
    Task<DailyClosureResponseDto?> GetLatestClosureAsync(CancellationToken cancellationToken = default);
    Task<string?> GetCashierDisplayNameAsync(int userId, CancellationToken cancellationToken = default);

    // 8.7-B5: los comprobantes se escriben DESPUÉS del commit de la transacción Serializable,
    // nunca dentro de ella (evita I/O de disco bloqueando aislamiento Serializable).
    Task WriteClosedClosureReceiptsAsync(DailyClosureResponseDto closure, CancellationToken cancellationToken = default);
}
