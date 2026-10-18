using Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

public partial class SalesService
{
    /// <summary>
    /// T3a (F2b, Paso 11/12, S3/S6/S7): registro de abonos CxC. El abono se imputa FIFO de
    /// cartera (vencimiento, emisión, Id) o a las facturas explícitas en el orden informado; la
    /// tasa del día es server-side (fuente BCV cacheada, snapshot inmutable en PaymentRate) y el
    /// diferencial cambiario realizado se registra con signo por asignación. Solo el dinero
    /// efectivamente cobrado entra a la gaveta (método efectivo → CashTransaction Income con
    /// origen CreditPayment). Todo corre en UNA transacción bajo advisory lock por cliente
    /// (clase 3 = cartera CxC) y la clave de idempotencia replica el cobro sin nuevos movimientos.
    /// </summary>
    public async Task<CreditPaymentResultDto> RegisterCreditPaymentAsync(
        CreditPaymentRequest request,
        int? actingUserId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.AmountUSD <= 0m)
        {
            throw new ArgumentException("El monto del abono debe ser mayor a cero (USD).");
        }

        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey) && request.IdempotencyKey.Trim().Length > 100)
        {
            throw new ArgumentException("La clave de idempotencia del abono excede la longitud máxima permitida (100 caracteres).");
        }

        if (!string.IsNullOrWhiteSpace(request.ReferenceNumber) && request.ReferenceNumber.Trim().Length > 100)
        {
            throw new ArgumentException("El número de referencia del abono excede la longitud máxima permitida (100 caracteres).");
        }

        int retryAttempt = 0;
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Reintento transitorio: el tracker se limpia para re-leer el estado fresco (mismo
            // contrato del núcleo de cierre de venta).
            if (retryAttempt++ > 0 || _context.Database.IsRelational())
            {
                _context.ChangeTracker.Clear();
            }

            await using var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                : null;

            try
            {
                // Lock por cliente: serializa los abonos concurrentes del mismo cliente para que
                // FIFO, tope de saldo y replay de idempotencia se resuelvan sobre estado estable
                // (misma técnica y clase del cierre a crédito).
                if (_context.Database.IsNpgsql())
                {
                    await _context.Database.ExecuteSqlRawAsync(
                        "SELECT pg_advisory_xact_lock({0}, {1})",
                        new object[] { 3, request.CustomerId },
                        cancellationToken);
                }

                // El replay se resuelve ANTES de validar la cartera: un cobro ya aplicado puede
                // haber saldado las facturas y el reintento debe replicar, no fallar.
                var replay = await FindExistingCreditPaymentAsync(request, cancellationToken);
                if (replay != null)
                {
                    return replay;
                }

                var customer = await _context.Customers
                    .AsNoTracking()
                    .FirstOrDefaultAsync(candidate => candidate.Id == request.CustomerId, cancellationToken)
                    ?? throw new KeyNotFoundException($"Cliente {request.CustomerId} no encontrado.");

                var paymentMethod = await _context.PaymentMethods
                    .AsNoTracking()
                    .FirstOrDefaultAsync(method => method.Id == request.PaymentMethodId, cancellationToken);
                if (paymentMethod == null || !paymentMethod.IsActive)
                {
                    throw new ArgumentException($"Método de pago inválido o inactivo: PaymentMethodId={request.PaymentMethodId}. Verifique la configuración de métodos de pago.");
                }

                var receivables = await LoadCreditPaymentReceivablesAsync(request, cancellationToken);
                if (receivables.Count == 0)
                {
                    throw new ArgumentException("El cliente no posee cuentas por cobrar abiertas con saldo pendiente para imputar el abono.");
                }

                decimal totalOpenUsd = Math.Round(receivables.Sum(receivable => receivable.BalanceUSD), 4, MidpointRounding.AwayFromZero);

                // Tope duro sin saldo a favor: el exceso solo se absorbe dentro del polvo decimal
                // (≤ 0.005 USD), que se recorta para saldar exactamente la deuda (S7).
                if (request.AmountUSD > totalOpenUsd + CreditService.BalanceToleranceUsd)
                {
                    throw new ArgumentException($"El abono de ${request.AmountUSD:F2} excede el saldo pendiente del cliente (${totalOpenUsd:F2}). No se admiten saldos a favor.");
                }

                decimal effectiveAmountUsd = Math.Min(request.AmountUSD, totalOpenUsd);
                decimal paymentRate = await ResolveCreditPaymentRateAsync(cancellationToken);

                var allocations = new List<CreditPaymentAllocation>();
                decimal allocatedUsd = 0m;
                decimal allocatedBsS = 0m;
                decimal realizedFxBsS = 0m;
                decimal remainingUsd = effectiveAmountUsd;
                var venezuelaToday = TimeZoneHelper.GetVenezuelaDate();

                foreach (var receivable in receivables)
                {
                    if (remainingUsd <= 0m)
                    {
                        break;
                    }

                    decimal appliedUsd = Math.Round(Math.Min(remainingUsd, receivable.BalanceUSD), 4, MidpointRounding.AwayFromZero);
                    if (appliedUsd <= 0m)
                    {
                        continue;
                    }

                    // FX realizado por asignación: contravalor a la tasa del abono menos el
                    // contravalor a la tasa histórica de emisión (signo: ganancia + / pérdida −).
                    // El principal USD nunca se revalúa.
                    decimal amountBsS = Math.Round(appliedUsd * paymentRate, 2, MidpointRounding.AwayFromZero);
                    decimal historicalBsS = Math.Round(appliedUsd * receivable.HistoricalRate, 2, MidpointRounding.AwayFromZero);
                    decimal fxDifferenceBsS = amountBsS - historicalBsS;

                    // Polvo decimal (S7): un residuo ≤ 0.005 USD se absorbe llevando el saldo a 0
                    // exacto para no dejar centésimas colgadas tras el pago total en bolívares.
                    decimal newBalanceUsd = Math.Round(receivable.BalanceUSD - appliedUsd, 4, MidpointRounding.AwayFromZero);
                    if (newBalanceUsd <= CreditService.BalanceToleranceUsd)
                    {
                        newBalanceUsd = 0m;
                    }

                    receivable.BalanceUSD = newBalanceUsd;
                    receivable.Status = newBalanceUsd == 0m
                        ? ReceivableStatus.Pagada
                        : (CreditService.IsOverdueBeyondGrace(receivable.DueDateUtc, customer.CreditGraceDays, venezuelaToday)
                            ? ReceivableStatus.Vencida
                            : ReceivableStatus.Parcial);

                    allocations.Add(new CreditPaymentAllocation
                    {
                        ReceivableId = receivable.Id,
                        Receivable = receivable,
                        AmountUSD = appliedUsd,
                        AmountBsS = amountBsS,
                        FxDifferenceBsS = fxDifferenceBsS
                    });

                    allocatedUsd = Math.Round(allocatedUsd + appliedUsd, 4, MidpointRounding.AwayFromZero);
                    allocatedBsS += amountBsS;
                    realizedFxBsS += fxDifferenceBsS;
                    remainingUsd = Math.Round(remainingUsd - appliedUsd, 4, MidpointRounding.AwayFromZero);
                }

                CashTransaction? cashTransaction = null;
                if (paymentMethod.IsCash && allocatedUsd > 0m)
                {
                    // Solo lo cobrado entra a la gaveta: mismo alta automática de sesión/carry-over
                    // del cobro de contado y misma convención IsPhysicalCash = true para efectivo.
                    var activeSession = await _cashDrawerService.GetOrCreateActiveSessionAsync(paymentRate, cancellationToken);
                    cashTransaction = new CashTransaction
                    {
                        SessionId = activeSession.Id,
                        Type = CashTransactionType.Income,
                        Source = CashTransactionSource.CreditPayment,
                        AmountUsd = allocatedUsd,
                        ExchangeRate = paymentRate,
                        AmountLocal = allocatedBsS,
                        IsPhysicalCash = true,
                        Description = $"Abono CxC {customer.Name} (Cliente #{customer.Id})",
                        TransactionTime = DateTime.UtcNow,
                        PaymentMethodId = request.PaymentMethodId
                    };
                    _context.CashTransactions.Add(cashTransaction);
                }

                var payment = new ReceivablePayment
                {
                    CustomerId = customer.Id,
                    AmountUSD = allocatedUsd,
                    AmountBsS = allocatedBsS,
                    PaymentRate = paymentRate,
                    FxDifferenceBsS = realizedFxBsS,
                    PaymentMethodId = request.PaymentMethodId,
                    ReferenceNumber = string.IsNullOrWhiteSpace(request.ReferenceNumber) ? null : request.ReferenceNumber.Trim(),
                    ReceivedAtUtc = DateTime.UtcNow,
                    IdempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim(),
                    ReceivedByUserId = actingUserId ?? request.ReceivedByUserId,
                    CashTransaction = cashTransaction
                };
                foreach (var allocation in allocations)
                {
                    payment.Allocations.Add(allocation);
                }

                _context.ReceivablePayments.Add(payment);
                await _context.SaveChangesAsync(cancellationToken);

                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                }

                _logger?.LogInformation(
                    "[SalesService] Abono CxC cliente #{CustomerId}: ${AmountUsd} (Bs.S {AmountBsS}) a tasa {Rate}; FX realizado Bs.S {Fx} en {Allocations} factura(s).",
                    customer.Id, allocatedUsd, allocatedBsS, paymentRate, realizedFxBsS, allocations.Count);

                return MapCreditPaymentResult(payment, isReplay: false);
            }
            catch
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                }
                throw;
            }
        });
    }

    /// <summary>
    /// Replay idempotente del abono: una clave ya consumida devuelve el cobro existente tal
    /// como quedó persistido (sin nueva caja ni nuevas imputaciones). Una clave reutilizada por
    /// otro cliente es un conflicto explícito (el índice único filtrado es la defensa final).
    /// </summary>
    private async Task<CreditPaymentResultDto?> FindExistingCreditPaymentAsync(CreditPaymentRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            return null;
        }

        string idempotencyKey = request.IdempotencyKey.Trim();

        var existing = await _context.ReceivablePayments
            .AsNoTracking()
            .AsSplitQuery()
            .Include(payment => payment.Allocations)
                .ThenInclude(allocation => allocation.Receivable)
                    .ThenInclude(receivable => receivable.Sale)
            .FirstOrDefaultAsync(payment => payment.IdempotencyKey == idempotencyKey, cancellationToken);

        if (existing == null)
        {
            return null;
        }

        if (existing.CustomerId != request.CustomerId)
        {
            throw new InvalidOperationException($"La clave de idempotencia '{idempotencyKey}' ya fue utilizada en un abono de otro cliente.");
        }

        return MapCreditPaymentResult(existing, isReplay: true);
    }

    /// <summary>
    /// Cartera a imputar: facturas abiertas (Vigente/Parcial/Vencida) con saldo por encima del
    /// polvo decimal. La imputación explícita exige pertenencia y vigencia de TODAS las facturas
    /// informadas y respeta el orden recibido; sin ids, el orden es FIFO de cartera
    /// (vencimiento, emisión, Id).
    /// </summary>
    private async Task<List<Receivable>> LoadCreditPaymentReceivablesAsync(CreditPaymentRequest request, CancellationToken cancellationToken)
    {
        var openStatuses = new[] { ReceivableStatus.Vigente, ReceivableStatus.Parcial, ReceivableStatus.Vencida };

        var query = _context.Receivables
            .Include(receivable => receivable.Sale)
            .Where(receivable => receivable.CustomerId == request.CustomerId
                && openStatuses.Contains(receivable.Status)
                && receivable.BalanceUSD > CreditService.BalanceToleranceUsd);

        if (request.ReceivableIds is { Count: > 0 })
        {
            var receivableIds = request.ReceivableIds;
            if (receivableIds.Distinct().Count() != receivableIds.Count)
            {
                throw new ArgumentException("La imputación explícita del abono contiene cuentas por cobrar repetidas.");
            }

            var selected = await query
                .Where(receivable => receivableIds.Contains(receivable.Id))
                .ToListAsync(cancellationToken);

            if (selected.Count != receivableIds.Count)
            {
                throw new ArgumentException("La imputación explícita del abono incluye cuentas por cobrar que no existen, no pertenecen al cliente o no están abiertas con saldo pendiente.");
            }

            var byId = selected.ToDictionary(receivable => receivable.Id);
            return receivableIds.Select(id => byId[id]).ToList();
        }

        return await query
            .OrderBy(receivable => receivable.DueDateUtc)
            .ThenBy(receivable => receivable.IssuedAtUtc)
            .ThenBy(receivable => receivable.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Tasa del abono server-side: la tasa BCV efectiva del día desde la misma fuente cacheada
    /// del checkout (nunca un valor del cliente). Sin tasa registrada el cobro falla cerrado y
    /// la tasa se congela con el techo de 2dp de la convención de caja.
    /// </summary>
    private async Task<decimal> ResolveCreditPaymentRateAsync(CancellationToken cancellationToken)
    {
        decimal officialRate;
        try
        {
            officialRate = await _inventoryService.GetTodayExchangeRateAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[SalesService] Error obteniendo la tasa BCV del día para el abono CxC.");
            throw new InvalidOperationException("No se pudo obtener la tasa BCV del día para registrar el abono. Verifique la tasa del día e intente nuevamente.");
        }

        if (officialRate <= 0m)
        {
            throw new InvalidOperationException("No se puede registrar el abono: no existe una tasa BCV registrada para hoy. Registre la tasa del día antes de cobrar.");
        }

        return PricingCalculator.RoundExchangeRateCeiling(officialRate);
    }

    private static CreditPaymentResultDto MapCreditPaymentResult(ReceivablePayment payment, bool isReplay)
    {
        return new CreditPaymentResultDto
        {
            PaymentId = payment.Id,
            CustomerId = payment.CustomerId,
            AmountUSD = payment.AmountUSD,
            AmountBsS = payment.AmountBsS,
            PaymentRate = payment.PaymentRate,
            FxDifferenceBsS = payment.FxDifferenceBsS,
            ReceivedAtUtc = payment.ReceivedAtUtc,
            ReceivedByUserId = payment.ReceivedByUserId,
            CashTransactionId = payment.CashTransactionId,
            PaymentMethodId = payment.PaymentMethodId,
            ReferenceNumber = payment.ReferenceNumber,
            IsReplay = isReplay,
            Allocations = payment.Allocations.Select(allocation => new CreditPaymentAllocationResultDto
            {
                ReceivableId = allocation.ReceivableId,
                SaleId = allocation.Receivable?.SaleId ?? 0,
                InvoiceNumber = allocation.Receivable?.Sale?.InvoiceNumber,
                AmountUSD = allocation.AmountUSD,
                AmountBsS = allocation.AmountBsS,
                FxDifferenceBsS = allocation.FxDifferenceBsS,
                BalanceAfterUSD = allocation.Receivable?.BalanceUSD ?? 0m,
                Status = allocation.Receivable?.Status ?? ReceivableStatus.Pagada
            }).ToList()
        };
    }
}
