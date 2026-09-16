using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Core.Helpers;
using Core.Interfaces;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

public class CashAdvanceCoordinator
{
    private readonly SalesDbContext _context;
    private readonly ISalesService _salesService;
    private readonly ICashDrawerService _cashDrawerService;
    private readonly ISystemSettingsService _settingsService;

    public CashAdvanceCoordinator(
        SalesDbContext context,
        ISalesService salesService,
        ICashDrawerService cashDrawerService,
        ISystemSettingsService settingsService)
    {
        _context = context;
        _salesService = salesService;
        _cashDrawerService = cashDrawerService;
        _settingsService = settingsService;
    }

    public async Task<CashAdvanceResultDto> ProcessAsync(
        int sessionId,
        decimal requestedAmountLocal,
        int paymentMethodId,
        string paymentMethodName,
        bool isTransfer,
        decimal exchangeRate,
        int? cashierId = null,
        string? userName = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentMethodName);

        if (requestedAmountLocal <= 0)
        {
            throw new ArgumentException("El monto del adelanto debe ser mayor a cero.", nameof(requestedAmountLocal));
        }

        if (requestedAmountLocal % 1 != 0)
        {
            throw new ArgumentException("El monto de efectivo a entregar debe ser un número entero sin decimales.", nameof(requestedAmountLocal));
        }

        var availableCash = await _cashDrawerService.GetCurrentBalanceLocalAsync(sessionId);
        var roundedRequested = Math.Round(requestedAmountLocal, 2, MidpointRounding.AwayFromZero);

        if (availableCash < roundedRequested)
        {
            throw new InvalidOperationException($"Saldo de efectivo en caja insuficiente. Disponible: {availableCash:N2} Bs.S, Requerido: {roundedRequested:N2} Bs.S.");
        }

        var commissionPercentage = await ResolveCommissionPercentageAsync(isTransfer, cancellationToken);
        var activeUserName = !string.IsNullOrWhiteSpace(userName) ? userName : "Usuario";

        return await _context.Database.CreateExecutionStrategy().ExecuteAsync<object?, CashAdvanceResultDto>(
            state: null,
            operation: (_, _, ct) => ProcessEnvelopeAsync(
                sessionId: sessionId,
                roundedRequested: roundedRequested,
                commissionPercentage: commissionPercentage,
                paymentMethodId: paymentMethodId,
                paymentMethodName: paymentMethodName,
                isTransfer: isTransfer,
                exchangeRate: exchangeRate,
                cashierId: cashierId,
                activeUserName: activeUserName,
                cancellationToken: ct),
            verifySucceeded: null,
            cancellationToken: cancellationToken);
    }

    private async Task<CashAdvanceResultDto> ProcessEnvelopeAsync(
        int sessionId,
        decimal roundedRequested,
        decimal commissionPercentage,
        int paymentMethodId,
        string paymentMethodName,
        bool isTransfer,
        decimal exchangeRate,
        int? cashierId,
        string activeUserName,
        CancellationToken cancellationToken)
    {
        IDbContextTransaction? dbTransaction = null;
        if (_context.Database.ProviderName != null && !_context.Database.ProviderName.Contains("InMemory"))
        {
            dbTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        }

        try
        {
            decimal commissionAmountLocal = Math.Round(roundedRequested * (commissionPercentage / 100.0m), 2, MidpointRounding.AwayFromZero);
            decimal totalChargedLocal = roundedRequested + commissionAmountLocal;

            decimal anchoredRate = exchangeRate;
            Sale? createdSale = await _salesService.CreateCashAdvanceSaleAsync(
                requestedAmountLocal: roundedRequested,
                commissionAmountLocal: commissionAmountLocal,
                paymentMethodId: paymentMethodId,
                paymentMethodName: paymentMethodName,
                isTransfer: isTransfer,
                exchangeRate: exchangeRate,
                cashierId: cashierId,
                userName: activeUserName,
                existingTransaction: dbTransaction
            );

            if (createdSale != null && createdSale.AppliedRate > 0m)
            {
                anchoredRate = createdSale.AppliedRate;
            }

            var expenseTx = await _cashDrawerService.AddTransactionAsync(
                sessionId: sessionId,
                type: CashTransactionType.Expense,
                source: CashTransactionSource.CashAdvance,
                amountLocal: roundedRequested,
                amountUsd: anchoredRate > 0 ? roundedRequested / anchoredRate : 0,
                exchangeRate: anchoredRate,
                description: $"Adelanto de Efectivo - {paymentMethodName} {commissionPercentage:0}% {activeUserName}",
                isPhysicalCash: true,
                paymentMethodId: paymentMethodId
            );

            var incomeTx = await _cashDrawerService.AddTransactionAsync(
                sessionId: sessionId,
                type: CashTransactionType.Income,
                source: CashTransactionSource.CashAdvance,
                amountLocal: commissionAmountLocal,
                amountUsd: anchoredRate > 0 ? commissionAmountLocal / anchoredRate : 0,
                exchangeRate: anchoredRate,
                description: $"Comisión Adelanto ({commissionPercentage:0}% {paymentMethodName}) - {activeUserName}",
                isPhysicalCash: false,
                paymentMethodId: paymentMethodId
            );

            if (dbTransaction != null)
            {
                await dbTransaction.CommitAsync(cancellationToken);
                await dbTransaction.DisposeAsync();
            }

            return new CashAdvanceResultDto
            {
                ExpenseTransaction = expenseTx,
                IncomeTransaction = incomeTx,
                RequestedAmountLocal = roundedRequested,
                CommissionAmountLocal = commissionAmountLocal,
                TotalChargedLocal = totalChargedLocal,
                CommissionPercentage = commissionPercentage,
                RelatedSaleId = createdSale?.Id,
                InvoiceNumber = createdSale?.InvoiceNumber
            };
        }
        catch
        {
            if (dbTransaction != null)
            {
                await dbTransaction.RollbackAsync(CancellationToken.None);
                await dbTransaction.DisposeAsync();
            }
            throw;
        }
    }

    private async Task<decimal> ResolveCommissionPercentageAsync(bool isTransfer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = isTransfer
            ? Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct
            : Core.Constants.SettingKeys.CashAdvanceCashCommissionPct;

        var value = await _settingsService.GetSettingAsync(key);

        if (!decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var percentage) || percentage <= 0)
        {
            Core.Logging.AppLogger.LogWarn($"[CashAdvanceCoordinator] Comisión de adelanto {(isTransfer ? "transferencia" : "efectivo")} no configurada o inválida (clave '{key}', valor '{value}'). Rechazando procesamiento.");
            throw new InvalidOperationException(
                $"La comisión de adelanto de efectivo ({(isTransfer ? "transferencia" : "efectivo")}) no está configurada o es inválida. " +
                $"Configure la clave '{key}' en SystemSettings antes de procesar adelantos.");
        }

        return percentage;
    }
}
