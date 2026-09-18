using Sales.Module.DTOs;
using Sales.Module.Entities;
using System.Threading.Tasks;

namespace Sales.Module.Interfaces;

public class CashAdvanceResultDto
{
    public CashTransactionResponseDto ExpenseTransaction { get; set; } = null!;
    public CashTransactionResponseDto IncomeTransaction { get; set; } = null!;
    public decimal RequestedAmountLocal { get; set; }
    public decimal CommissionAmountLocal { get; set; }
    public decimal TotalChargedLocal { get; set; }
    public decimal CommissionPercentage { get; set; }
    public int? RelatedSaleId { get; set; }
    public int? InvoiceNumber { get; set; }
}

public interface ICashDrawerService
{
    Task<CashDrawerSessionResponseDto?> GetActiveSessionAsync();

    /// <summary>
    /// Sesión activa con sus transacciones de caja precargadas (retorna null si no hay sesión abierta).
    /// Solo los endpoints que exponen el detalle de movimientos deben usarla (8.5-M1: el include completo
    /// no debe ejecutarse en cada acceso interno).
    /// </summary>
    Task<CashDrawerSessionResponseDto?> GetActiveSessionWithTransactionsAsync();
    Task<CashDrawerSessionResponseDto> GetOrCreateActiveSessionAsync(decimal currentExchangeRate);
    Task<CashDrawerSessionResponseDto> OpenSessionAsync(decimal openingBalanceLocal, decimal currentExchangeRate);
    Task<CashDrawerSessionResponseDto> CloseSessionAsync(decimal actualClosingBalanceLocal, decimal currentExchangeRate);

    /// <summary>
    /// Cierra la sesión activa y abre una nueva conservando el saldo esperado en caja (saldo teórico acumulado:
    /// apertura + ingresos - egresos de la sesión que se cierra, independiente de los montos declarados del arqueo)
    /// pero reiniciando a 0 los acumuladores de ingresos y egresos de la sesión.
    /// </summary>
    Task RolloverSessionAfterClosureAsync(decimal currentExchangeRate);

    Task<CashTransactionResponseDto> AddTransactionAsync(
        int sessionId,
        CashTransactionType type,
        CashTransactionSource source,
        decimal amountLocal,
        decimal amountUsd,
        decimal exchangeRate,
        string description,
        int? referenceId = null,
        bool isPhysicalCash = true,
        int? paymentMethodId = null);

    /// <summary>
    /// Registra el vuelto de una venta como egreso físico de caja (Source=SalePayment) VALIDANDO saldo
    /// (8.6-C1): usa el advisory lock de la sesión y verifica que el saldo disponible (saldo base + ingresos
    /// cash pendientes de la venta aún no persistidos) soporte el vuelto ANTES de insertarlo, eliminando la
    /// vía a saldo de caja negativo. Debe ejecutarse dentro de la transacción compartida de completar venta.
    /// </summary>
    Task<CashTransactionResponseDto> RecordSaleChangeAsync(
        int sessionId,
        decimal changeUsd,
        decimal changeBsS,
        decimal exchangeRate,
        string description,
        int saleId,
        int? cashPaymentMethodId,
        decimal pendingCashIncomeBsS = 0m);

    Task<decimal> GetCurrentBalanceLocalAsync(int sessionId);

    /// <summary>
    /// Historial persistente de movimientos de caja: devuelve los movimientos físicos más recientes
    /// de TODAS las sesiones (activa y anteriores), para conservar la trazabilidad de las sesiones
    /// cerradas junto con los movimientos de la sesión siguiente.
    /// </summary>
    Task<System.Collections.Generic.List<CashTransactionResponseDto>> GetHistoryAsync(int limit = 300);
}
