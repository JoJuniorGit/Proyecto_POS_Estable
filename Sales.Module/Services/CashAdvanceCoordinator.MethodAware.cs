using Core.Common;
using Core.DTOs;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

public partial class CashAdvanceCoordinator
{
    private sealed record AdvanceSides(
        int DispensedMethodId,
        string DispensedMethodName,
        string DispensedCurrency,
        decimal DispensedAmount,
        decimal DispensedUsd,
        int ReceivedMethodId,
        string ReceivedMethodName,
        string ReceivedCurrency,
        decimal ReceivedPrincipal,
        decimal ReceivedCommission,
        decimal ReceivedCommissionUsd,
        decimal ReceivedTotal,
        decimal TotalUsd,
        decimal ExchangeRate);

    private async Task<CashAdvanceResultDto> ProcessMethodAwareAsync(
        int sessionId,
        decimal requestedAmountLocal,
        int paymentMethodId,
        string paymentMethodName,
        bool isTransfer,
        decimal exchangeRate,
        int? cashierId,
        string? userName,
        CancellationToken cancellationToken,
        CashAdvanceIdempotency? idempotency,
        int? dispensedCashPaymentMethodId,
        decimal? dispensedAmount,
        int? receivedPaymentMethodId)
    {
        var commissionPercentage = await ResolveCommissionPercentageAsync(isTransfer, cancellationToken);
        var activeUserName = !string.IsNullOrWhiteSpace(userName) ? userName : "Usuario";
        var sides = await ResolveAdvanceSidesAsync(
            requestedAmountLocal,
            paymentMethodId,
            receivedPaymentMethodId,
            dispensedCashPaymentMethodId,
            dispensedAmount,
            exchangeRate,
            commissionPercentage,
            cancellationToken);

        var result = await _context.Database.CreateExecutionStrategy().ExecuteAsync<object?, CashAdvanceResultDto?>(
            state: null,
            operation: async (_, _, ct) => (CashAdvanceResultDto?)await ProcessMethodAwareEnvelopeAsync(
                sessionId,
                sides,
                commissionPercentage,
                isTransfer,
                cashierId,
                activeUserName,
                ct,
                idempotency),
            verifySucceeded: async (_, _, ct) =>
            {
                bool exists = await IdempotentAdvanceExistsAsync(idempotency, ct);
                return new ExecutionResult<CashAdvanceResultDto?>(exists, default);
            },
            cancellationToken: cancellationToken);

        if (result is not null)
        {
            return result;
        }

        _context.ChangeTracker.Clear();
        if (idempotency is null)
        {
            throw new InvalidOperationException("No se pudo confirmar el resultado del adelanto de efectivo.");
        }

        return await TryReadIdempotentAdvanceAsync(idempotency, cancellationToken)
            ?? throw new InvalidOperationException("No se pudo recuperar el resultado confirmado del adelanto de efectivo.");
    }

    private async Task<CashAdvanceResultDto> ProcessMethodAwareEnvelopeAsync(
        int sessionId,
        AdvanceSides sides,
        decimal commissionPercentage,
        bool isTransfer,
        int? cashierId,
        string activeUserName,
        CancellationToken cancellationToken,
        CashAdvanceIdempotency? idempotency)
    {
        _context.ChangeTracker.Clear();
        IDbContextTransaction? dbTransaction = null;
        if (_context.Database.ProviderName != null && !_context.Database.ProviderName.Contains("InMemory"))
        {
            dbTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        }

        try
        {
            await _cashDrawerService.LockOpenSessionForWriteAsync(sessionId, cancellationToken);

            if (idempotency is not null)
            {
                var replay = await TryReadIdempotentAdvanceAsync(idempotency, cancellationToken);
                if (replay is not null)
                {
                    if (dbTransaction is not null)
                    {
                        await dbTransaction.CommitAsync(cancellationToken);
                        await dbTransaction.DisposeAsync();
                    }
                    return replay;
                }
            }

            var methodBalances = await _cashDrawerService.GetPhysicalMethodBalancesAsync(sessionId, cancellationToken);
            var dispensedBalance = methodBalances?
                .FirstOrDefault(balance => balance.PaymentMethodId == sides.DispensedMethodId);
            if (dispensedBalance is not null)
            {
                if (dispensedBalance.CurrentBalance < sides.DispensedAmount)
                {
                    throw InsufficientMethodBalance(sides, dispensedBalance);
                }
            }
            else
            {
                // Legacy/mock sessions without WU1 rows still validate the aggregate local drawer.
                decimal legacyBalance = await _cashDrawerService.GetCurrentBalanceLocalAsync(sessionId, cancellationToken);
                if (legacyBalance < sides.DispensedAmount)
                {
                    throw new InvalidOperationException(
                        $"Saldo de efectivo en caja insuficiente para el monto solicitado. Disponible: {legacyBalance:N2} {sides.DispensedCurrency}.");
                }
            }

            SaleDto? createdSale = await _salesService.CreateCashAdvanceSaleAsync(
                requestedAmountLocal: sides.DispensedAmount,
                commissionAmountLocal: sides.ReceivedCommission,
                paymentMethodId: sides.ReceivedMethodId,
                paymentMethodName: sides.ReceivedMethodName,
                isTransfer: isTransfer,
                exchangeRate: sides.ExchangeRate,
                cashierId: cashierId,
                userName: activeUserName,
                existingTransaction: dbTransaction,
                cancellationToken: cancellationToken,
                receivedPrincipal: sides.ReceivedPrincipal,
                receivedCommission: sides.ReceivedCommission,
                receivedTotal: sides.ReceivedTotal,
                receivedTotalUsd: sides.TotalUsd,
                receivedCurrencyCode: sides.ReceivedCurrency,
                receivedCommissionUsd: sides.ReceivedCommissionUsd);

            var expenseTx = await _cashDrawerService.AddTransactionAsync(
                sessionId: sessionId,
                type: CashTransactionType.Expense,
                source: CashTransactionSource.CashAdvance,
                amountLocal: sides.DispensedAmount,
                amountUsd: sides.DispensedUsd,
                exchangeRate: sides.ExchangeRate,
                description: $"Adelanto de Efectivo - {sides.ReceivedMethodName} {commissionPercentage:0}% {activeUserName}",
                referenceId: createdSale?.Id,
                isPhysicalCash: true,
                paymentMethodId: sides.DispensedMethodId,
                cancellationToken: cancellationToken,
                resolvedCurrencyCode: sides.DispensedCurrency);

            var incomeTx = await _cashDrawerService.AddTransactionAsync(
                sessionId: sessionId,
                type: CashTransactionType.Income,
                source: CashTransactionSource.CashAdvance,
                amountLocal: sides.ReceivedCommission,
                amountUsd: sides.ReceivedCommissionUsd,
                exchangeRate: sides.ExchangeRate,
                description: $"Comisión Adelanto ({commissionPercentage:0}% {sides.ReceivedMethodName}) - {activeUserName}",
                referenceId: createdSale?.Id,
                isPhysicalCash: false,
                paymentMethodId: sides.ReceivedMethodId,
                cancellationToken: cancellationToken,
                resolvedCurrencyCode: sides.ReceivedCurrency);

            var result = new CashAdvanceResultDto
            {
                ExpenseTransaction = expenseTx,
                IncomeTransaction = incomeTx,
                DispensedCashPaymentMethodId = sides.DispensedMethodId,
                DispensedCurrencyCode = sides.DispensedCurrency,
                DispensedAmount = sides.DispensedAmount,
                ReceivedPaymentMethodId = sides.ReceivedMethodId,
                ReceivedCurrencyCode = sides.ReceivedCurrency,
                ReceivedAmount = sides.ReceivedPrincipal,
                ReceivedCommissionAmount = sides.ReceivedCommission,
                ReceivedTotalAmount = sides.ReceivedTotal,
                RequestedAmountLocal = sides.DispensedAmount,
                CommissionAmountLocal = sides.ReceivedCommission,
                TotalChargedLocal = sides.ReceivedTotal,
                CommissionPercentage = commissionPercentage,
                RelatedSaleId = createdSale?.Id,
                InvoiceNumber = createdSale?.InvoiceNumber
            };

            if (idempotency is not null)
            {
                await PersistIdempotentAdvanceAsync(idempotency, result, cancellationToken);
            }

            if (dbTransaction is not null)
            {
                await dbTransaction.CommitAsync(cancellationToken);
                await dbTransaction.DisposeAsync();
            }

            return result;
        }
        catch (DbUpdateException ex) when (idempotency is not null && IsIdempotentRequestUniqueViolation(ex))
        {
            if (dbTransaction is not null)
            {
                try
                {
                    await dbTransaction.RollbackAsync(CancellationToken.None);
                }
                catch
                {
                    // The connection may be gone; dispose closes the attempt.
                }
                await dbTransaction.DisposeAsync();
            }

            _context.ChangeTracker.Clear();
            var replay = await TryReadIdempotentAdvanceAsync(idempotency, CancellationToken.None);
            if (replay is not null)
            {
                return replay;
            }

            throw;
        }
        catch
        {
            if (dbTransaction is not null)
            {
                await dbTransaction.RollbackAsync(CancellationToken.None);
                await dbTransaction.DisposeAsync();
            }
            throw;
        }
    }

    private static InvalidOperationException InsufficientMethodBalance(
        AdvanceSides sides,
        PhysicalMethodBalanceDto balance)
        => new(
            $"Existencia insuficiente en {sides.DispensedMethodName} ({sides.DispensedCurrency}): " +
            $"disponible {balance.CurrencySymbol}{balance.CurrentBalance:N2}, " +
            $"requerido {balance.CurrencySymbol}{sides.DispensedAmount:N2}.");

    private async Task<AdvanceSides> ResolveAdvanceSidesAsync(
        decimal requestedAmountLocal,
        int legacyPaymentMethodId,
        int? receivedPaymentMethodId,
        int? dispensedCashPaymentMethodId,
        decimal? dispensedAmount,
        decimal exchangeRate,
        decimal commissionPercentage,
        CancellationToken cancellationToken)
    {
        var profile = await ResolveProfileOrNullAsync(cancellationToken);
        var localCurrency = profile is null
            ? PaymentMethodCurrencyResolver.LocalCurrency
            : CashDrawerBalanceService.ResolveLocalCurrency(profile);
        var allowedCurrencies = CashDrawerBalanceService.ResolveAllowedCurrencies(profile, localCurrency);

        int receivedId = receivedPaymentMethodId ?? legacyPaymentMethodId;
        if (receivedId <= 0)
        {
            throw new ArgumentException("Debe seleccionar un método electrónico receptor.", nameof(receivedPaymentMethodId));
        }

        var receiver = await _context.PaymentMethods
            .AsNoTracking()
            .FirstOrDefaultAsync(method => method.Id == receivedId && method.IsActive && !method.IsDeleted, cancellationToken)
            ?? throw new ArgumentException($"El método de pago con ID {receivedId} no existe o se encuentra inactivo.", nameof(receivedPaymentMethodId));

        if (receiver.IsCash)
        {
            throw new ArgumentException("El adelanto de efectivo solo puede cobrarse mediante métodos de pago digitales (Punto de Venta o Transferencia).", nameof(receivedPaymentMethodId));
        }

        string receiverCurrency = NormalizeCurrency(receiver)
            ?? throw new ArgumentException("La moneda del método receptor no es válida.", nameof(receivedPaymentMethodId));
        if (!allowedCurrencies.Contains(receiverCurrency))
        {
            throw new ArgumentException("El método receptor no pertenece al régimen monetario actual.", nameof(receivedPaymentMethodId));
        }

        var physicalMethods = await _context.PaymentMethods
            .AsNoTracking()
            .Where(method => method.IsCash && method.IsActive && !method.IsDeleted)
            .OrderBy(method => method.DisplayOrder)
            .ThenBy(method => method.Id)
            .ToListAsync(cancellationToken);

        var validPhysicalMethods = physicalMethods
            .Where(method => NormalizeCurrency(method) is { } currency && allowedCurrencies.Contains(currency))
            .ToList();

        PaymentMethod? dispensed = null;
        if (dispensedCashPaymentMethodId is > 0)
        {
            dispensed = validPhysicalMethods.FirstOrDefault(method => method.Id == dispensedCashPaymentMethodId.Value)
                ?? throw new ArgumentException(
                    $"El método físico {dispensedCashPaymentMethodId} no está activo o no pertenece al régimen monetario actual.",
                    nameof(dispensedCashPaymentMethodId));
        }
        else if (validPhysicalMethods.Count == 1)
        {
            dispensed = validPhysicalMethods[0];
        }
        else if (validPhysicalMethods.Count == 0)
        {
            throw new InvalidOperationException("No hay un método físico válido para dispensar el adelanto.");
        }
        else
        {
            throw new ArgumentException(
                "Hay más de un método físico disponible. Seleccione el método que entrega el efectivo (DispensedCashPaymentMethodId).",
                nameof(dispensedCashPaymentMethodId));
        }

        string dispensedCurrency = NormalizeCurrency(dispensed)
            ?? throw new ArgumentException("La moneda del método dispensador no es válida.", nameof(dispensedCashPaymentMethodId));

        decimal amount = dispensedAmount ?? requestedAmountLocal;
        if (amount <= 0m)
        {
            throw new ArgumentException("El monto del adelanto debe ser mayor a cero.", nameof(dispensedAmount));
        }

        bool allowsFractional = profile is null
            ? false
            : profile.Mode == MonetaryMode.Single
                || (profile.Mode == MonetaryMode.Dual
                    && !string.Equals(
                        CurrencyCodeNormalizer.NormalizeToIso(profile.SecondaryCurrencyCode),
                        "VES",
                        StringComparison.Ordinal));

        if (string.Equals(dispensedCurrency, "VES", StringComparison.Ordinal)
            && !allowsFractional
            && amount % 1m != 0m)
        {
            throw new ArgumentException("El monto de efectivo a entregar debe ser un número entero sin decimales.", nameof(dispensedAmount));
        }

        amount = Math.Round(amount, 2, MidpointRounding.AwayFromZero);
        decimal rate = exchangeRate;
        bool dispensedIsUsd = string.Equals(dispensedCurrency, "USD", StringComparison.Ordinal);
        bool receivedIsUsd = string.Equals(receiverCurrency, "USD", StringComparison.Ordinal);
        if (rate <= 0m && !(dispensedIsUsd && receivedIsUsd))
        {
            throw new ArgumentException("La tasa de cambio debe ser mayor a cero para el cruce de monedas.", nameof(exchangeRate));
        }

        decimal dispensedUsd = dispensedIsUsd
            ? amount
            : Math.Round(amount / rate, 4, MidpointRounding.AwayFromZero);

        decimal receivedPrincipal;
        if (receivedIsUsd)
        {
            receivedPrincipal = dispensedUsd;
        }
        else if (string.Equals(dispensedCurrency, receiverCurrency, StringComparison.Ordinal))
        {
            receivedPrincipal = amount;
        }
        else
        {
            receivedPrincipal = Math.Round(dispensedUsd * rate, 2, MidpointRounding.AwayFromZero);
        }

        decimal commission = Math.Round(receivedPrincipal * (commissionPercentage / 100m), 2, MidpointRounding.AwayFromZero);
        decimal receivedTotal = receivedPrincipal + commission;
        decimal totalUsd = receivedIsUsd
            ? receivedTotal
            : Math.Round(receivedTotal / rate, 4, MidpointRounding.AwayFromZero);
        decimal commissionUsd = receivedIsUsd
            ? commission
            : Math.Round(commission / rate, 4, MidpointRounding.AwayFromZero);

        return new AdvanceSides(
            dispensed.Id,
            dispensed.Name,
            dispensedCurrency,
            amount,
            dispensedUsd,
            receiver.Id,
            receiver.Name,
            receiverCurrency,
            receivedPrincipal,
            commission,
            commissionUsd,
            receivedTotal,
            totalUsd,
            rate);
    }

    private async Task<Core.DTOs.MonetaryProfileDto?> ResolveProfileOrNullAsync(CancellationToken cancellationToken)
    {
        if (_monetaryProfileService is null)
        {
            return null;
        }

        try
        {
            return await _monetaryProfileService.GetAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "No se pudo leer el perfil monetario para validar el adelanto. Verifique la configuración monetaria.",
                ex);
        }
    }

    private static string? NormalizeCurrency(PaymentMethod method)
        => CurrencyCodeNormalizer.NormalizeToIso(
            PaymentMethodCurrencyResolver.ResolveStoredOrName(method.Currency, method.Name));
}
