using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Core.Common;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

public class CashAdvanceCoordinator
{
    private readonly SalesDbContext _context;
    private readonly ISalesService _salesService;
    private readonly ICashDrawerService _cashDrawerService;
    private readonly ISystemSettingsService _settingsService;
    // 8.211-T3c3c1 (S4): local cash precision depends on the profile regime. Null keeps the
    // historical integer rule (legacy/fail-safe).
    private readonly IMonetaryProfileService? _monetaryProfileService;

    public CashAdvanceCoordinator(
        SalesDbContext context,
        ISalesService salesService,
        ICashDrawerService cashDrawerService,
        ISystemSettingsService settingsService,
        IMonetaryProfileService? monetaryProfileService = null)
    {
        _context = context;
        _salesService = salesService;
        _cashDrawerService = cashDrawerService;
        _settingsService = settingsService;
        _monetaryProfileService = monetaryProfileService;
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
        CancellationToken cancellationToken = default,
        CashAdvanceIdempotency? idempotency = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentMethodName);

        if (requestedAmountLocal <= 0)
        {
            throw new ArgumentException("El monto del adelanto debe ser mayor a cero.", nameof(requestedAmountLocal));
        }

        // 8.211-T3c3c1 (S4): integer local cash is VES-specific. Single and Dual non-VES
        // secondaries accept their minor unit; legacy/fail-safe keeps the historical integer rule.
        if (!await AllowsFractionalLocalCashAsync(cancellationToken)
            && requestedAmountLocal % 1 != 0)
        {
            throw new ArgumentException("El monto de efectivo a entregar debe ser un número entero sin decimales.", nameof(requestedAmountLocal));
        }

        // 8.168 (SEC-04-RES): validar que el método de pago exista, esté activo y sea digital cuando el catálogo está poblado.
        if (paymentMethodId > 0 && await _context.PaymentMethods.AnyAsync(cancellationToken))
        {
            var paymentMethod = await _context.PaymentMethods
                .AsNoTracking()
                .FirstOrDefaultAsync(pm => pm.Id == paymentMethodId && pm.IsActive && !pm.IsDeleted, cancellationToken)
                ?? throw new ArgumentException($"El método de pago con ID {paymentMethodId} no existe o se encuentra inactivo.", nameof(paymentMethodId));

            if (paymentMethod.IsCash)
            {
                throw new ArgumentException("El adelanto de efectivo solo puede cobrarse mediante métodos de pago digitales (Punto de Venta o Transferencia).", nameof(paymentMethodId));
            }
        }

        var availableCash = await _cashDrawerService.GetCurrentBalanceLocalAsync(sessionId, cancellationToken);
        var roundedRequested = Math.Round(requestedAmountLocal, 2, MidpointRounding.AwayFromZero);

        if (availableCash < roundedRequested)
        {
            throw new InvalidOperationException("Saldo de efectivo en caja insuficiente para el monto solicitado.");
        }

        var commissionPercentage = await ResolveCommissionPercentageAsync(isTransfer, cancellationToken);
        var activeUserName = !string.IsNullOrWhiteSpace(userName) ? userName : "Usuario";

        var result = await _context.Database.CreateExecutionStrategy().ExecuteAsync<object?, CashAdvanceResultDto?>(
            state: null,
            operation: async (_, _, ct) => (CashAdvanceResultDto?)await ProcessEnvelopeAsync(
                sessionId: sessionId,
                roundedRequested: roundedRequested,
                commissionPercentage: commissionPercentage,
                paymentMethodId: paymentMethodId,
                paymentMethodName: paymentMethodName,
                isTransfer: isTransfer,
                exchangeRate: exchangeRate,
                cashierId: cashierId,
                activeUserName: activeUserName,
                cancellationToken: ct,
                idempotency: idempotency),
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

        // 8.202-H2 (S5): verifySucceeded=true significa que el commit sí ocurrió aunque la
        // conexión se cortó. Se recupera la respuesta persistida en lugar de repetir el adelanto.
        _context.ChangeTracker.Clear();
        if (idempotency is null)
        {
            throw new InvalidOperationException("No se pudo confirmar el resultado del adelanto de efectivo.");
        }

        return await TryReadIdempotentAdvanceAsync(idempotency, cancellationToken)
            ?? throw new InvalidOperationException("No se pudo recuperar el resultado confirmado del adelanto de efectivo.");
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
            // 8.202-H2 (S4): el lock de gaveta se toma antes de cualquier efecto y revalida Open.
            // El adelanto no toma locks de venta/cliente previos: crea una venta nueva, por lo que
            // el orden canónico (venta/cliente -> sesión) no se invierte.
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

            decimal commissionAmountLocal = Math.Round(roundedRequested * (commissionPercentage / 100.0m), 2, MidpointRounding.AwayFromZero);
            decimal totalChargedLocal = roundedRequested + commissionAmountLocal;

            decimal anchoredRate = exchangeRate;
            SaleDto? createdSale = await _salesService.CreateCashAdvanceSaleAsync(
                requestedAmountLocal: roundedRequested,
                commissionAmountLocal: commissionAmountLocal,
                paymentMethodId: paymentMethodId,
                paymentMethodName: paymentMethodName,
                isTransfer: isTransfer,
                exchangeRate: exchangeRate,
                cashierId: cashierId,
                userName: activeUserName,
                existingTransaction: dbTransaction,
                cancellationToken: cancellationToken
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
                paymentMethodId: paymentMethodId,
                cancellationToken: cancellationToken
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
                paymentMethodId: paymentMethodId,
                cancellationToken: cancellationToken
            );

            var result = new CashAdvanceResultDto
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

            if (idempotency is not null)
            {
                // Misma transacción que la venta y los dos movimientos: si el insert choca con una
                // clave concurrente, el rollback revierte todo el envelope.
                await PersistIdempotentAdvanceAsync(idempotency, result, cancellationToken);
            }

            if (dbTransaction != null)
            {
                await dbTransaction.CommitAsync(cancellationToken);
                await dbTransaction.DisposeAsync();
            }

            return result;
        }
        catch (DbUpdateException ex) when (idempotency is not null && IsIdempotentRequestUniqueViolation(ex))
        {
            if (dbTransaction != null)
            {
                try
                {
                    await dbTransaction.RollbackAsync(CancellationToken.None);
                }
                catch
                {
                    // La conexión puede haberse perdido; el dispose cierra el intento.
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
            if (dbTransaction != null)
            {
                await dbTransaction.RollbackAsync(CancellationToken.None);
                await dbTransaction.DisposeAsync();
            }
            throw;
        }
    }

    private async Task<CashAdvanceResultDto?> TryReadIdempotentAdvanceAsync(
        CashAdvanceIdempotency idempotency,
        CancellationToken cancellationToken)
    {
        var existing = await _context.IdempotentRequests
            .AsNoTracking()
            .FirstOrDefaultAsync(
                record => record.Key == idempotency.Key && record.RequestPath == idempotency.RequestPath,
                cancellationToken);

        if (existing is null || existing.ExpiresAtUtc <= DateTime.UtcNow)
        {
            return null;
        }

        if (existing.UserId != idempotency.UserId
            || existing.PayloadHash.Length != idempotency.PayloadHash.Length
            || !CryptographicOperations.FixedTimeEquals(existing.PayloadHash, idempotency.PayloadHash)
            || string.IsNullOrWhiteSpace(existing.ResponseBody))
        {
            return null;
        }

        return JsonSerializer.Deserialize<CashAdvanceResultDto>(existing.ResponseBody);
    }

    private async Task PersistIdempotentAdvanceAsync(
        CashAdvanceIdempotency idempotency,
        CashAdvanceResultDto result,
        CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        _context.IdempotentRequests.Add(new IdempotentRequest
        {
            Key = idempotency.Key,
            RequestPath = idempotency.RequestPath,
            PayloadHash = idempotency.PayloadHash,
            StatusCode = 200,
            ResponseBody = idempotency.SerializeResponse(result),
            UserId = idempotency.UserId,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddHours(24)
        });

        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> IdempotentAdvanceExistsAsync(
        CashAdvanceIdempotency? idempotency,
        CancellationToken cancellationToken)
    {
        if (idempotency is null)
        {
            return false;
        }

        return await _context.IdempotentRequests
            .AsNoTracking()
            .AnyAsync(
                record => record.Key == idempotency.Key && record.RequestPath == idempotency.RequestPath,
                cancellationToken);
    }

    private static bool IsIdempotentRequestUniqueViolation(DbUpdateException ex)
    {
        if (ex.InnerException is Npgsql.PostgresException postgresException)
        {
            return postgresException.SqlState == "23505"
                && (string.IsNullOrEmpty(postgresException.ConstraintName)
                    || postgresException.ConstraintName.Contains("IdempotentRequests", StringComparison.Ordinal));
        }

        return ex.Message.Contains("IdempotentRequests", StringComparison.Ordinal)
            || ex.InnerException?.Message.Contains("IdempotentRequests", StringComparison.Ordinal) == true;
    }

    /// <summary>
    /// 8.211-T3c3c1 (S4): Single cash is fractional (2dp primary) and Dual non-VES uses the
    /// secondary minor unit. Dual USD/VES, a malformed profile, or a failed read keeps the
    /// historical integer rule (fail-safe).
    /// </summary>
    private async Task<bool> AllowsFractionalLocalCashAsync(CancellationToken cancellationToken)
    {
        if (_monetaryProfileService is null)
        {
            return false;
        }

        try
        {
            var profile = await _monetaryProfileService.GetAsync(cancellationToken);
            if (profile.Mode == MonetaryMode.Single)
            {
                return true;
            }

            if (profile.Mode != MonetaryMode.Dual)
            {
                return false;
            }

            var secondary = CurrencyCodeNormalizer.NormalizeToIso(profile.SecondaryCurrencyCode);
            return secondary is not null
                && !string.Equals(secondary, "VES", StringComparison.Ordinal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Core.Logging.AppLogger.LogWarn(
                $"[CashAdvanceCoordinator] No se pudo leer el perfil monetario; se conserva la regla de efectivo entero. Error: {ex.Message}",
                nameof(CashAdvanceCoordinator));
            return false;
        }
    }

    public async Task<decimal?> TryGetCommissionPercentageAsync(bool isTransfer, CancellationToken cancellationToken = default)
        => await ResolveCommissionCoreAsync(isTransfer, cancellationToken);

    private async Task<decimal?> ResolveCommissionCoreAsync(bool isTransfer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var key = isTransfer
            ? Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct
            : Core.Constants.SettingKeys.CashAdvanceCashCommissionPct;

        var value = await _settingsService.GetSettingAsync(key);

        if (!decimal.TryParse(value, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var percentage) || percentage <= 0)
        {
            Core.Logging.AppLogger.LogWarn($"[CashAdvanceCoordinator] Comisión de adelanto {(isTransfer ? "transferencia" : "efectivo")} no configurada o inválida (clave '{key}', valor '{value}'). Rechazando procesamiento.");
            return null;
        }

        return percentage;
    }

    private async Task<decimal> ResolveCommissionPercentageAsync(bool isTransfer, CancellationToken cancellationToken = default)
    {
        var percentage = await ResolveCommissionCoreAsync(isTransfer, cancellationToken);

        if (!percentage.HasValue)
        {
            var key = isTransfer
                ? Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct
                : Core.Constants.SettingKeys.CashAdvanceCashCommissionPct;

            throw new InvalidOperationException(
                $"La comisión de adelanto de efectivo ({(isTransfer ? "transferencia" : "efectivo")}) no está configurada o es inválida. " +
                $"Configure la clave '{key}' en SystemSettings antes de procesar adelantos.");
        }

        return percentage.Value;
    }
}
