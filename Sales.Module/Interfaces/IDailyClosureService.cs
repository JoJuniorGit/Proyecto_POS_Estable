using Sales.Module.Entities;
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
}

public sealed record CloseShiftResult(
    int ClosureId,
    string CashierName,
    string CashierCedula,
    DateTime ClosedAt,
    decimal ExchangeRate,
    IReadOnlyList<ShiftReportDetailResult> Details);

public sealed record ShiftReportDetailResult(
    int PaymentMethodId,
    string PaymentMethodName,
    string Currency,
    decimal DeclaredAmount,
    decimal SystemAmount,
    decimal Difference,
    string Status);

public interface IDailyClosureService
{
    Task<List<ExpectedTotalDto>> GetExpectedTotalsByPaymentMethodAsync(DateTime dateUtc, CancellationToken cancellationToken = default);
    Task<DailyClosure> CreateClosureAsync(DailyClosure closure);
    Task<CloseShiftResult> CreateClosureFromCommandAsync(CreateClosureCommand command, CancellationToken cancellationToken);
    Task<DailyClosure?> GetClosureAsync(int id);

    // 8.7-B5: los comprobantes se escriben DESPUÉS del commit de la transacción Serializable,
    // nunca dentro de ella (evita I/O de disco bloqueando aislamiento Serializable).
    Task WriteClosedClosureReceiptsAsync(DailyClosure closure, CancellationToken cancellationToken = default);
}
