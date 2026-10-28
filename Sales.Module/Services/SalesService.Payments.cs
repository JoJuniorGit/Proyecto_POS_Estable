using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Core.Events;
using Core.Helpers;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Sales.Module.DTOs;
using Sales.Module.Entities;

namespace Sales.Module.Services;

public partial class SalesService
{


    public async Task<SaleDto> AddPaymentToHoldSaleAsync(int saleId, AddPaymentRequestDto request, string? idempotencyKey = null, byte[]? idempotencyPayloadHash = null, int? actingUserId = null, System.Threading.CancellationToken cancellationToken = default)
    {
        // 8.211-T3c3b2 (S7): Single add-payment support is deferred; reject before any mutation.
        var monetaryProfile = await ResolveMonetaryProfileSnapshotAsync(cancellationToken);
        EnsureSupportedCheckoutMonetaryProfile(monetaryProfile, allowSingle: false);

        Sale? sale = null;
        if (!_context.Database.IsRelational())
        {
            sale = await GetSaleEntityAsync(saleId);
            EnsureHoldClaimAccess(sale, actingUserId);
            if (sale.Status != SaleStatus.OnHold)
                throw new InvalidOperationException("Solo se pueden agregar abonos a ventas en estado en espera.");
        }

        // 8.9-B4: envolver en execution strategy (reintento completo ante fallos transitorios).
        int paymentRetryAttempt = 0;
        return await _context.Database.CreateExecutionStrategy().ExecuteAsync(async _ =>
        {
        if (paymentRetryAttempt++ > 0 || _context.Database.IsRelational())
        {
            _context.ChangeTracker.Clear();
            sale = await GetSaleEntityAsync(saleId);
            EnsureHoldClaimAccess(sale, actingUserId);
            if (sale.Status != SaleStatus.OnHold)
                throw new InvalidOperationException("Solo se pueden agregar abonos a ventas en estado en espera.");
        }
        IDbContextTransaction? dbTransaction = null;
        if (_context.Database.ProviderName != null && !_context.Database.ProviderName.Contains("InMemory"))
        {
            dbTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
        }

        try
        {
            if (_context.Database.IsNpgsql())
            {
                await _context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0}, {1})", new object[] { 2, saleId }, cancellationToken);
            }

            var freshSale = await _context.Sales
                .Where(s => s.Id == saleId)
                .Select(s => new { s.Status, s.TotalUSD, s.TotalBsS, s.AppliedRate })
                .FirstOrDefaultAsync(cancellationToken);
            if (freshSale == null)
            {
                throw new KeyNotFoundException($"Sale {saleId} not found.");
            }
            if (freshSale.Status != SaleStatus.OnHold)
            {
                throw new InvalidOperationException("Solo se pueden agregar abonos a ventas en estado en espera.");
            }

            // 8.185-T3 (guard dual): remanentes en ambos dominios contra el total EFECTIVO Bs.S.
            var existingPayments = await _context.SalePayments
                .Where(sp => sp.SaleId == saleId)
                .Select(sp => new { sp.Amount, sp.AmountBsS })
                .ToListAsync(cancellationToken);
            decimal runningPaidUsd = existingPayments.Sum(p => p.Amount);
            decimal runningPaidBsS = existingPayments.Sum(p => p.AmountBsS > 0m ? p.AmountBsS : PricingCalculator.ToBsS(p.Amount, freshSale.AppliedRate));

            var input = await ComputePaymentInputsAsync(saleId, monetaryProfile, freshSale.TotalUSD, freshSale.TotalBsS, freshSale.AppliedRate, runningPaidUsd, runningPaidBsS, request, "AddPaymentToHoldSale");

            var currencyCode = ResolvePaymentMethodCurrency(input.Method);
            var paymentEntity = new SalePayment
            {
                SaleId = sale!.Id,
                PaymentMethodId = request.PaymentMethodId,
                Amount = Math.Round(input.AmountUsd, 2, MidpointRounding.AwayFromZero),
                AmountBsS = Math.Round(input.AmountBsS, 2, MidpointRounding.AwayFromZero),
                ExchangeRate = input.Rate,
                ReferenceNumber = request.ReferenceNumber,
                CreatedAt = DateTime.UtcNow,
                CurrencyCode = currencyCode,
                OriginalAmount = ResolveOriginalAmount(input.AmountUsd, input.AmountBsS, currencyCode)
            };

            _context.SalePayments.Add(paymentEntity);

            // Si es efectivo y monto positivo, registrar en sesión activa de caja
            if (input.Method != null && input.Method.IsCash && input.AmountUsd > 0 && _cashDrawerService != null)
            {
                var activeSession = await _cashDrawerService.GetOrCreateActiveSessionAsync(input.Rate, cancellationToken);
                // 8.202-H1 (S2): venta (clase 2) -> sesión; el lock cubre el alta del movimiento.
                await _cashDrawerService.LockOpenSessionForWriteAsync(activeSession.Id, cancellationToken);
                var cashTx = new CashTransaction
                {
                    SessionId = activeSession.Id,
                    Type = CashTransactionType.Income,
                    Source = CashTransactionSource.SalePayment,
                    AmountUsd = input.AmountUsd,
                    ExchangeRate = input.Rate,
                    AmountLocal = Math.Round(input.AmountBsS, 2, MidpointRounding.AwayFromZero),
                    IsPhysicalCash = true,
                    Description = $"Abono Venta #{saleId}",
                    TransactionTime = DateTime.UtcNow,
                    SaleId = sale.Id,
                    PaymentMethodId = request.PaymentMethodId,
                    // 8.211-T3b3 (S6): snapshot de la moneda fisica del abono.
                    CurrencyCode = currencyCode
                };
                _context.CashTransactions.Add(cashTx);
            }

            RegisterIdempotencyRecord(idempotencyKey, idempotencyPayloadHash, $"/api/sales/{saleId}/payments", System.Text.Json.JsonSerializer.Serialize(MapToDto(sale)), actingUserId);

            await _context.SaveChangesAsync(cancellationToken);

            if (dbTransaction != null)
            {
                await dbTransaction.CommitAsync(cancellationToken);
            }

            await NotifyHoldOrdersChangedAsync();
            return MapToDto(sale);
        }
        catch (Exception ex)
        {
            if (dbTransaction != null)
            {
                await dbTransaction.RollbackAsync(cancellationToken);
            }
            _logger?.LogError(ex, "[SalesService] Error al registrar abono en venta #{SaleId}. Transacción revertida.", saleId);
            throw;
        }
        finally
        {
            dbTransaction?.Dispose();
        }
        }, cancellationToken);
    }

    // 8.29-A05: abonos atómicos por lote. La validación y la persistencia de todos los abonos del
    // lote comparten UNA sola transacción (rollback conjunto) y UNA sola SaveChanges; el advisory
    // lock por venta serializa el cálculo contra el estado fresco. Idempotency: un único
    // Idempotency-Key por lote.
    public async Task<SaleDto> AddPaymentsBatchToHoldSaleAsync(int saleId, List<AddPaymentRequestDto> payments, string? idempotencyKey = null, byte[]? idempotencyPayloadHash = null, int? actingUserId = null, System.Threading.CancellationToken cancellationToken = default)
    {
        if (payments == null || payments.Count == 0)
            throw new ArgumentException("Debe enviar al menos un abono.");

        if (payments.Count > 50)
            throw new ArgumentException("El lote de abonos supera el máximo permitido (50).");

        // 8.211-T3c3b2 (S7): Single batch add-payment support is deferred; reject before mutation.
        var monetaryProfile = await ResolveMonetaryProfileSnapshotAsync(cancellationToken);
        EnsureSupportedCheckoutMonetaryProfile(monetaryProfile, allowSingle: false);

        Sale? sale = null;
        if (!_context.Database.IsRelational())
        {
            sale = await GetSaleEntityAsync(saleId);
            EnsureHoldClaimAccess(sale, actingUserId);
            if (sale.Status != SaleStatus.OnHold)
                throw new InvalidOperationException("Solo se pueden agregar abonos a ventas en estado en espera.");
        }

        // 8.9-B4: execution strategy para reintentos completos del lote ante fallos transitorios.
        int batchRetryAttempt = 0;
        return await _context.Database.CreateExecutionStrategy().ExecuteAsync(async _ =>
        {
            if (batchRetryAttempt++ > 0 || _context.Database.IsRelational())
            {
                _context.ChangeTracker.Clear();
                sale = await GetSaleEntityAsync(saleId);
                EnsureHoldClaimAccess(sale, actingUserId);
                if (sale.Status != SaleStatus.OnHold)
                    throw new InvalidOperationException("Solo se pueden agregar abonos a ventas en estado en espera.");
            }
            IDbContextTransaction? dbTransaction = null;
            if (_context.Database.ProviderName != null && !_context.Database.ProviderName.Contains("InMemory"))
            {
                dbTransaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            }

            try
            {
                if (_context.Database.IsNpgsql())
                {
                    await _context.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock({0}, {1})", new object[] { 2, saleId }, cancellationToken);
                }

                var freshSale = await _context.Sales
                    .Where(s => s.Id == saleId)
                    .Select(s => new { s.Status, s.TotalUSD, s.TotalBsS, s.AppliedRate })
                    .FirstOrDefaultAsync(cancellationToken);
                if (freshSale == null)
                {
                    throw new KeyNotFoundException($"Sale {saleId} not found.");
                }
                if (freshSale.Status != SaleStatus.OnHold)
                {
                    throw new InvalidOperationException("Solo se pueden agregar abonos a ventas en estado en espera.");
                }

                // 8.185-T3 (guard dual): remanentes en ambos dominios contra el total EFECTIVO Bs.S.
                var existingPayments = await _context.SalePayments
                    .Where(sp => sp.SaleId == saleId)
                    .Select(sp => new { sp.Amount, sp.AmountBsS })
                    .ToListAsync(cancellationToken);
                decimal runningPaidUsd = existingPayments.Sum(p => p.Amount);
                decimal runningPaidBsS = existingPayments.Sum(p => p.AmountBsS > 0m ? p.AmountBsS : PricingCalculator.ToBsS(p.Amount, freshSale.AppliedRate));

                var requestedMethodIds = payments.Select(p => p.PaymentMethodId).Distinct().ToList();
                var preloadedMethods = await _context.PaymentMethods
                    .AsNoTracking()
                    .IgnoreQueryFilters()
                    .Where(pm => requestedMethodIds.Contains(pm.Id))
                    .ToDictionaryAsync(pm => pm.Id, cancellationToken);
                bool hasConfiguredPaymentMethods = preloadedMethods.Count > 0
                    || await _context.PaymentMethods.AsNoTracking().IgnoreQueryFilters().AnyAsync(cancellationToken);

                var computedInputs = new List<(AddPaymentRequestDto Request, ComputedPaymentInput Input)>(payments.Count);
                foreach (var request in payments)
                {
                    var input = await ComputePaymentInputsAsync(saleId, monetaryProfile, freshSale.TotalUSD, freshSale.TotalBsS, freshSale.AppliedRate, runningPaidUsd, runningPaidBsS, request, "AddPaymentsBatchToHoldSale", preloadedMethods, hasConfiguredPaymentMethods);
                    runningPaidUsd += input.AmountUsd;
                    runningPaidBsS += input.AmountBsS;
                    computedInputs.Add((request, input));
                }

                CashDrawerSessionResponseDto? activeSession = null;
                var firstCashInput = computedInputs.FirstOrDefault(ci => ci.Input.Method != null && ci.Input.Method.IsCash && ci.Input.AmountUsd > 0);
                if (firstCashInput.Input != null && _cashDrawerService != null)
                {
                    activeSession = await _cashDrawerService.GetOrCreateActiveSessionAsync(firstCashInput.Input.Rate, cancellationToken);
                    // 8.202-H1 (S2): un lock por lote cubre todos los abonos en efectivo.
                    await _cashDrawerService.LockOpenSessionForWriteAsync(activeSession.Id, cancellationToken);
                }

                foreach (var (request, input) in computedInputs)
                {
                    var currencyCode = ResolvePaymentMethodCurrency(input.Method);
                    var paymentEntity = new SalePayment
                    {
                        SaleId = sale!.Id,
                        PaymentMethodId = request.PaymentMethodId,
                        Amount = Math.Round(input.AmountUsd, 2, MidpointRounding.AwayFromZero),
                        AmountBsS = Math.Round(input.AmountBsS, 2, MidpointRounding.AwayFromZero),
                        ExchangeRate = input.Rate,
                        ReferenceNumber = request.ReferenceNumber,
                        CreatedAt = DateTime.UtcNow,
                        CurrencyCode = currencyCode,
                        OriginalAmount = ResolveOriginalAmount(input.AmountUsd, input.AmountBsS, currencyCode)
                    };

                    _context.SalePayments.Add(paymentEntity);

                    if (input.Method != null && input.Method.IsCash && input.AmountUsd > 0 && activeSession != null)
                    {
                        var cashTx = new CashTransaction
                        {
                            SessionId = activeSession.Id,
                            Type = CashTransactionType.Income,
                            Source = CashTransactionSource.SalePayment,
                            AmountUsd = input.AmountUsd,
                            ExchangeRate = input.Rate,
                            AmountLocal = Math.Round(input.AmountBsS, 2, MidpointRounding.AwayFromZero),
                            IsPhysicalCash = true,
                            Description = $"Abono Venta #{saleId}",
                            TransactionTime = DateTime.UtcNow,
                            SaleId = sale.Id,
                            PaymentMethodId = request.PaymentMethodId,
                            // 8.211-T3b3 (S6): snapshot de la moneda fisica del abono.
                            CurrencyCode = currencyCode
                        };
                        _context.CashTransactions.Add(cashTx);
                    }
                }

                RegisterIdempotencyRecord(idempotencyKey, idempotencyPayloadHash, $"/api/sales/{saleId}/payments/batch", System.Text.Json.JsonSerializer.Serialize(MapToDto(sale!)), actingUserId);

                await _context.SaveChangesAsync(cancellationToken);

                if (dbTransaction != null)
                {
                    await dbTransaction.CommitAsync(cancellationToken);
                }

                await NotifyHoldOrdersChangedAsync();
                return MapToDto(sale!);
            }
            catch (Exception ex)
            {
                if (dbTransaction != null)
                {
                    await dbTransaction.RollbackAsync(cancellationToken);
                }
                _logger?.LogError(ex, "[SalesService] Error al registrar abonos por lote en venta #{SaleId}. Transacción revertida.", saleId);
                throw;
            }
            finally
            {
                dbTransaction?.Dispose();
            }
        }, cancellationToken);
    }

    // Cálculo y validación compartidos de un abono individual (tasa anclada BCV, montos,
    // límite acumulado y regla de efectivo a montos enteros). Se usa tanto en el flujo de
    // abono simple como en el batch; `runningPaidUsd`/`runningPaidBsS` son lo ya abonado +
    // abonos del lote en cada dominio.
    private async Task<ComputedPaymentInput> ComputePaymentInputsAsync(
        int saleId,
        MonetaryProfileSnapshot monetaryProfile,
        decimal saleTotalUsd,
        decimal saleTotalBsS,
        decimal saleAppliedRate,
        decimal runningPaidUsd,
        decimal runningPaidBsS,
        AddPaymentRequestDto request,
        string contextLabel,
        Dictionary<int, PaymentMethod>? preloadedMethods = null,
        bool? preloadedHasConfiguredMethods = null)
    {
        // 8.6-B3/8.5-A5: Tasa del abono anclada a la BCV del día cuando el cliente la envía.
        decimal rate = request.ExchangeRate > 0
            ? await ResolveAnchoredRateAsync(request.ExchangeRate, contextLabel: contextLabel, referenceId: saleId)
            : saleAppliedRate;
        decimal amountUsd = request.AmountUSD > 0
            ? Math.Round(request.AmountUSD, 2, MidpointRounding.AwayFromZero)
            : (rate > 0 ? Math.Round(request.AmountBsS / rate, 2, MidpointRounding.AwayFromZero) : 0m);

        if (amountUsd <= 0 && request.AmountBsS <= 0)
        {
            throw new ArgumentException("El monto del abono debe ser mayor a cero.");
        }

        var (resolvedAmountUsd, resolvedAmountBsS) = ResolveConsistentPaymentAmounts(amountUsd, request.AmountBsS, rate);

        // 8.185-T3 (decisión mantenedor): guard DUAL — el exceso en USD que proviene del escalón de
        // la política cash no rechaza si el dominio Bs.S todavía cubre el total EFECTIVO (misma
        // familia que el gate de cobertura del completar venta). Con ExactMath (efectivo == canónico)
        // el comportamiento queda como hoy; subpagos y lote conservan su semántica.
        bool exceedsUsd = runningPaidUsd + resolvedAmountUsd > saleTotalUsd + 0.05m;
        bool exceedsBsS = runningPaidBsS + resolvedAmountBsS > saleTotalBsS + 0.05m * rate;
        if (exceedsUsd && exceedsBsS)
        {
            throw new ArgumentException("El monto del abono excede el total pendiente de la venta.");
        }

        // SEC-11: Validate payment method is active and not soft-deleted.
        PaymentMethod? method;
        bool hasConfiguredPaymentMethods;
        if (preloadedMethods != null)
        {
            preloadedMethods.TryGetValue(request.PaymentMethodId, out method);
            hasConfiguredPaymentMethods = preloadedHasConfiguredMethods
                ?? (method != null || await _context.PaymentMethods.AsNoTracking().IgnoreQueryFilters().AnyAsync());
        }
        else
        {
            method = await _context.PaymentMethods
                .AsNoTracking()
                .IgnoreQueryFilters()
                .FirstOrDefaultAsync(pm => pm.Id == request.PaymentMethodId);
            hasConfiguredPaymentMethods = method != null || await _context.PaymentMethods.AsNoTracking().IgnoreQueryFilters().AnyAsync();
        }
        if (hasConfiguredPaymentMethods && (method == null || !method.IsActive || method.IsDeleted))
        {
            throw new ArgumentException($"Método de pago inválido o inactivo: PaymentMethodId={request.PaymentMethodId}. Verifique la configuración de métodos de pago.");
        }

        if (method != null)
        {
            EnsurePaymentMethodCurrencyMatchesProfile(monetaryProfile, method);
        }

        // Validación de integridad: el efectivo solo acepta montos enteros (sin centavos).
        // Single se rechaza antes de este camino, por lo que la regla histórica Dual se conserva.
        if (method != null && method.IsCash && resolvedAmountBsS % 1 != 0)
        {
            throw new ArgumentException("El método de pago en efectivo solo acepta montos enteros.");
        }

        return new ComputedPaymentInput(rate, resolvedAmountUsd, resolvedAmountBsS, method);
    }

    private const decimal PaymentAmountConsistencyToleranceUsd = 0.01m;

    private static (decimal AmountUsd, decimal AmountBsS) ResolveConsistentPaymentAmounts(decimal amountUsd, decimal amountBsS, decimal rate)
    {
        if (amountUsd < 0m || amountBsS < 0m)
        {
            throw new ArgumentException("Los montos del pago no pueden ser negativos.");
        }

        if (amountUsd > 0m && amountBsS > 0m)
        {
            if (rate <= 0m)
            {
                throw new ArgumentException("La tasa de cambio debe ser mayor a cero.");
            }

            if (Math.Abs(amountBsS / rate - amountUsd) > PaymentAmountConsistencyToleranceUsd)
            {
                throw new ArgumentException("Los montos en USD y Bs.S del pago no son consistentes con la tasa aplicada.");
            }

            return (amountUsd, amountBsS);
        }

        if (amountUsd > 0m)
        {
            if (rate <= 0m)
            {
                throw new ArgumentException("La tasa de cambio debe ser mayor a cero.");
            }

            return (amountUsd, PricingCalculator.ToBsS(amountUsd, rate));
        }

        if (amountBsS > 0m)
        {
            if (rate <= 0m)
            {
                throw new ArgumentException("La tasa de cambio debe ser mayor a cero.");
            }

            return (PricingCalculator.ToUSD(amountBsS, rate), amountBsS);
        }

        return (0m, 0m);
    }

    // 8.211-T3b1 (S6/S8): moneda congelada del abono. La autoridad es la moneda PERSISTIDA del
    // método; el nombre solo se usa como fallback legacy; un método ausente cae a la moneda local.
    private static string ResolvePaymentMethodCurrency(PaymentMethod? method)
        => method is null
            ? PaymentMethodCurrencyResolver.LocalCurrency
            : PaymentMethodCurrencyResolver.ResolveStoredOrName(method.Currency, method.Name);

    // 8.211-T3c2 (S6): importe original del abono en la moneda del medio de pago. Local usa el
    // monto Bs.S y USD el monto dólar; una moneda fuera del vocabulario actual conserva el USD,
    // espejo de la ruta legacy habilitada en esta fase.
    private static decimal ResolveOriginalAmount(decimal amountUsd, decimal amountLocal, string? currencyCode)
        => string.Equals(currencyCode, PaymentMethodCurrencyResolver.Usd, StringComparison.Ordinal)
            ? amountUsd
            : string.Equals(currencyCode, PaymentMethodCurrencyResolver.LocalCurrency, StringComparison.Ordinal)
                ? amountLocal
                : amountUsd;

    private sealed record ComputedPaymentInput(decimal Rate, decimal AmountUsd, decimal AmountBsS, PaymentMethod? Method);
}
