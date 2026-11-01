using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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

/// <summary>
/// T3b2 (F2b, Paso 11/12, S5/S7): anulación sin pagos, devoluciones totales/parciales y
/// overpago de una venta a crédito. Reglas: la anulación simple solo procede sin pagos de ningún
/// tipo (saldo == original, sin asignaciones de abono y sin pago inicial de venta mixta) y no
/// genera caja; toda devolución reduce primero el saldo con la base EFECTIVA por línea, restituye
/// stock/cupo y, cuando supera el saldo pendiente, reembolsa el excedente efectivamente pagado
/// por caja (Expense, origen CreditRefund) a la tasa congelada del último pago recibido: el abono
/// CxC más reciente o, sin abonos, el pago inicial de la venta mixta. Sin modelo de saldo a
/// favor: el sobrante solo se absorbe dentro del polvo decimal (≤ 0.005 USD). Todo corre en UNA
/// transacción bajo advisory lock por cliente (clase 3).
/// </summary>
public partial class SalesService
{
    // Estados que representan deuda viva (S3): solo una obligación abierta admite devoluciones.
    private static readonly ReceivableStatus[] OpenCreditStatuses =
    {
        ReceivableStatus.Vigente,
        ReceivableStatus.Parcial,
        ReceivableStatus.Vencida
    };

    /// <summary>
    /// T3b2 (S7): anula una venta a crédito SIN pagos de ningún tipo (saldo vigente == original,
    /// sin asignaciones de abono y sin pago inicial de venta mixta). Restituye TODO el stock por el
    /// camino de inventario (motivo Return), marca la obligación Anulada con saldo 0 y la venta
    /// Cancelada; no genera movimientos de caja y el cupo se restituye implícitamente al dejar de
    /// computar la deuda. Con pago inicial el camino correcto es la devolución (reembolso por caja).
    /// </summary>
    public async Task<CreditCancelResultDto> CancelCreditSaleAsync(
        int saleId,
        string? reason,
        int? actingUserId = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(reason) && reason.Trim().Length > 500)
        {
            throw new ArgumentException("El motivo de la anulación excede la longitud máxima permitida (500 caracteres).");
        }

        int retryAttempt = 0;
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Reintento transitorio: el tracker se limpia para re-leer el estado fresco (mismo
            // contrato del núcleo de cierre y del cobro CxC).
            if (retryAttempt++ > 0 || _context.Database.IsRelational())
            {
                _context.ChangeTracker.Clear();
            }

            await using var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                : null;

            if (transaction != null && _inventoryService != null)
            {
                await _inventoryService.EnrollInTransactionAsync(transaction.GetDbTransaction(), cancellationToken);
            }

            try
            {
                var sale = await GetSaleEntityAsync(saleId);

                var receivableRef = await _context.Receivables
                    .AsNoTracking()
                    .Where(receivable => receivable.SaleId == saleId)
                    .Select(receivable => new { receivable.Id, receivable.CustomerId })
                    .FirstOrDefaultAsync(cancellationToken);
                if (receivableRef == null)
                {
                    throw new InvalidOperationException($"La venta #{saleId} no posee una cuenta por cobrar asociada; la anulación a crédito solo procede sobre ventas financiadas.");
                }

                // Sección crítica POR CLIENTE (clase 3 = cartera CxC): serializa la anulación con
                // abonos, devoluciones y otras anulaciones concurrentes del mismo cliente.
                if (_context.Database.IsNpgsql())
                {
                    await _context.Database.ExecuteSqlRawAsync(
                        "SELECT pg_advisory_xact_lock({0}, {1})",
                        new object[] { 3, receivableRef.CustomerId },
                        cancellationToken);
                }

                var receivable = await _context.Receivables.FirstAsync(
                    candidate => candidate.Id == receivableRef.Id,
                    cancellationToken);

                if (!OpenCreditStatuses.Contains(receivable.Status))
                {
                    throw new InvalidOperationException($"La cuenta por cobrar de la venta #{saleId} está cerrada ({receivable.Status}); la venta a crédito ya fue anulada o liquidada.");
                }

                // Anulación sin pagos (S7): solo procede cuando NADA se pagó. Cualquier asignación
                // de abono, un pago inicial de venta mixta (SalePayment) o un saldo distinto del
                // original bloquean la anulación simple: el pago inicial es dinero real y su
                // restitución exige el flujo de devolución (con salida de caja), no una anulación
                // que promete cero movimiento de caja.
                bool hasAllocations = await _context.CreditPaymentAllocations
                    .AsNoTracking()
                    .AnyAsync(allocation => allocation.ReceivableId == receivable.Id, cancellationToken);
                bool hasSalePayments = sale.Payments.Count > 0;
                if (hasAllocations || hasSalePayments || receivable.BalanceUSD != receivable.OriginalUSD)
                {
                    throw new InvalidOperationException($"No se puede anular la venta a crédito #{saleId}: la venta posee pagos registrados (pago inicial o abonos) o devoluciones previas (saldo ${receivable.BalanceUSD:F2} de ${receivable.OriginalUSD:F2}). Use el flujo de devolución para restituir el cupo.");
                }

                receivable.Status = ReceivableStatus.Anulada;
                receivable.BalanceUSD = 0m;
                sale.Status = SaleStatus.Cancelled;

                await RestockCreditSaleItemsAsync(sale, actingUserId, cancellationToken);

                await _context.SaveChangesAsync(cancellationToken);
                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    if (_inventoryService != null)
                    {
                        await _inventoryService.DetachFromTransactionAsync(cancellationToken);
                    }
                }

                _logger?.LogInformation(
                    "[SalesService] Venta a crédito #{SaleId} anulada sin abonos: stock restituido, obligación #{ReceivableId} anulada y cupo del cliente #{CustomerId} restaurado.",
                    sale.Id, receivable.Id, receivable.CustomerId);

                return new CreditCancelResultDto
                {
                    SaleId = sale.Id,
                    ReceivableId = receivable.Id,
                    CustomerId = receivable.CustomerId,
                    CancelledOriginalUSD = receivable.OriginalUSD,
                    Status = receivable.Status,
                    SaleStatus = sale.Status,
                    CancelledAtUtc = DateTime.UtcNow,
                    Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()
                };
            }
            catch
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    if (_inventoryService != null)
                    {
                        await _inventoryService.DetachFromTransactionAsync(CancellationToken.None);
                    }
                }
                throw;
            }
        });
    }

    /// <summary>
    /// T3b2 (S5/S7): registra la devolución total o parcial de una venta a crédito. El valor por
    /// línea sale de la base EFECTIVA (<c>SubtotalBsS</c> / tasa de emisión, 4dp) prorrateado por
    /// cantidad; la devolución reduce primero el saldo y restituye stock/cupo. Si el valor
    /// devuelto supera el saldo pendiente, el excedente efectivamente pagado (overpago) se
    /// reembolsa por caja (Expense, origen CreditRefund) a la tasa congelada del ÚLTIMO pago
    /// recibido: el abono CxC más reciente imputado a la obligación o, sin abonos, el pago inicial
    /// (SalePayment) más reciente de la venta mixta. El replay por <c>IdempotencyKey</c> replica
    /// sin nuevos movimientos.
    /// </summary>
    public async Task<CreditReturnResultDto> RegisterCreditReturnAsync(
        int saleId,
        CreditReturnRequest request,
        int? actingUserId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCreditReturnRequest(request);

        int retryAttempt = 0;
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (retryAttempt++ > 0 || _context.Database.IsRelational())
            {
                _context.ChangeTracker.Clear();
            }

            await using var transaction = _context.Database.IsRelational()
                ? await _context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                : null;

            if (transaction != null && _inventoryService != null)
            {
                await _inventoryService.EnrollInTransactionAsync(transaction.GetDbTransaction(), cancellationToken);
            }

            try
            {
                var sale = await GetSaleEntityAsync(saleId);

                var receivableRef = await _context.Receivables
                    .AsNoTracking()
                    .Where(receivable => receivable.SaleId == saleId)
                    .Select(receivable => new { receivable.Id, receivable.CustomerId })
                    .FirstOrDefaultAsync(cancellationToken);
                if (receivableRef == null)
                {
                    throw new InvalidOperationException($"La venta #{saleId} no posee una cuenta por cobrar asociada; la devolución solo procede sobre ventas financiadas.");
                }

                // Lock por cliente (clase 3): serializa devoluciones, abonos y anulación del mismo
                // cliente para que FIFO, acumulado devuelto y replay se resuelvan sobre estado estable.
                if (_context.Database.IsNpgsql())
                {
                    await _context.Database.ExecuteSqlRawAsync(
                        "SELECT pg_advisory_xact_lock({0}, {1})",
                        new object[] { 3, receivableRef.CustomerId },
                        cancellationToken);
                }

                // El replay se resuelve ANTES de validar la obligación: una devolución ya aplicada
                // pudo anular la cuenta por cobrar y el reintento debe replicar, no fallar.
                var replay = await FindExistingCreditReturnAsync(request.IdempotencyKey, saleId, cancellationToken);
                if (replay != null)
                {
                    if (transaction != null)
                    {
                        await transaction.CommitAsync(cancellationToken);
                        if (_inventoryService != null)
                        {
                            await _inventoryService.DetachFromTransactionAsync(cancellationToken);
                        }
                    }
                    return replay;
                }

                var receivable = await _context.Receivables.FirstAsync(
                    candidate => candidate.Id == receivableRef.Id,
                    cancellationToken);

                if (!OpenCreditStatuses.Contains(receivable.Status))
                {
                    throw new InvalidOperationException($"La cuenta por cobrar de la venta #{saleId} está cerrada ({receivable.Status}); no admite devoluciones.");
                }

                var saleItemsById = sale.Items.ToDictionary(item => item.Id);

                // Cantidades normalizadas a la precisión de la línea (18,3) antes de validar el
                // acumulado: el tope por ítem se mide sobre el valor persistido, no sobre el crudo.
                var requestedLines = new List<(SaleItem SaleItem, decimal Quantity)>();
                foreach (var itemRequest in request.Items)
                {
                    if (!saleItemsById.TryGetValue(itemRequest.SaleItemId, out var saleItem))
                    {
                        throw new ArgumentException($"El ítem {itemRequest.SaleItemId} no pertenece a la venta #{saleId}; no puede devolverse desde esta venta.");
                    }

                    if (saleItem.Quantity <= 0m)
                    {
                        throw new InvalidOperationException($"El ítem '{saleItem.ProductName}' no posee cantidad vendida registrada; no puede devolverse.");
                    }

                    decimal quantity = Math.Round(itemRequest.Quantity, 3, MidpointRounding.AwayFromZero);
                    if (quantity <= 0m)
                    {
                        throw new ArgumentException("La cantidad devuelta debe ser mayor a cero (mínimo 0.001).");
                    }

                    requestedLines.Add((saleItem, quantity));
                }

                var itemIds = requestedLines.Select(line => line.SaleItem.Id).Distinct().ToList();
                var cumulativeReturned = await _context.CreditReturnItems
                    .AsNoTracking()
                    .Where(item => itemIds.Contains(item.SaleItemId) && item.CreditReturn.SaleId == saleId)
                    .GroupBy(item => item.SaleItemId)
                    .Select(group => new { SaleItemId = group.Key, Quantity = group.Sum(item => item.Quantity) })
                    .ToDictionaryAsync(entry => entry.SaleItemId, entry => entry.Quantity, cancellationToken);

                decimal returnedUsd = 0m;
                var returnItems = new List<CreditReturnItem>();
                foreach (var (saleItem, quantity) in requestedLines)
                {
                    cumulativeReturned.TryGetValue(saleItem.Id, out decimal alreadyReturned);
                    if (alreadyReturned + quantity > saleItem.Quantity)
                    {
                        throw new ArgumentException($"La devolución del ítem '{saleItem.ProductName}' excede la cantidad vendida: vendida {saleItem.Quantity:G29}, ya devuelta {alreadyReturned:G29}, solicitada {quantity:G29}.");
                    }

                    // Valor desde la base EFECTIVA de la línea (S5/S7): SubtotalBsS ya incluye la
                    // política cash de la línea; el unitario efectivo se prorratea por cantidad.
                    decimal effectiveUnitUsd = PricingCalculator.ToUSD(saleItem.SubtotalBsS, sale.AppliedRate, 4);
                    decimal amountUsd = Math.Round(effectiveUnitUsd * (quantity / saleItem.Quantity), 4, MidpointRounding.AwayFromZero);
                    returnedUsd = Math.Round(returnedUsd + amountUsd, 4, MidpointRounding.AwayFromZero);

                    returnItems.Add(new CreditReturnItem
                    {
                        SaleItemId = saleItem.Id,
                        Quantity = quantity,
                        AmountUSD = amountUsd
                    });
                }

                decimal balanceBefore = receivable.BalanceUSD;
                bool hasPayments = await _context.CreditPaymentAllocations
                    .AsNoTracking()
                    .AnyAsync(allocation => allocation.ReceivableId == receivable.Id, cancellationToken);
                // El pago inicial de una venta mixta también es dinero recibido: habilita el
                // reembolso del overpago y conserva la factura (su ingreso sigue contando en Z).
                bool hasSalePayments = sale.Payments.Count > 0;
                bool hasReceivedMoney = hasPayments || hasSalePayments;

                // Overpago: excedente del valor devuelto sobre el saldo pendiente. Sin dinero
                // recibido el exceso solo puede ser polvo decimal (≤ 0.005 USD) y se absorbe sin
                // salida de caja (F1 no abre modelo de saldo a favor).
                decimal rawOverpayUsd = Math.Round(returnedUsd - balanceBefore, 4, MidpointRounding.AwayFromZero);
                decimal overpayUsd = rawOverpayUsd > CreditService.BalanceToleranceUsd && hasReceivedMoney ? rawOverpayUsd : 0m;

                decimal newBalance = Math.Round(balanceBefore - returnedUsd, 4, MidpointRounding.AwayFromZero);
                if (newBalance <= CreditService.BalanceToleranceUsd)
                {
                    newBalance = 0m;
                }

                decimal refundedUsd = 0m;
                decimal refundedBsS = 0m;
                decimal? refundRate = null;
                CashTransaction? refundCashTransaction = null;

                if (overpayUsd > 0m)
                {
                    // Último pago recibido para la venta, con su tasa/método congelados (snapshot
                    // inmutable): el abono CxC más reciente imputado a esta obligación o, si nunca
                    // hubo abonos, el pago inicial (SalePayment) más reciente del checkout; el
                    // reembolso restituye el valor efectivamente pagado a la tasa de ese pago.
                    var latestAllocation = await _context.ReceivablePayments
                        .AsNoTracking()
                        .Where(payment => payment.Allocations.Any(allocation => allocation.ReceivableId == receivable.Id))
                        .OrderByDescending(payment => payment.ReceivedAtUtc)
                        .ThenByDescending(payment => payment.Id)
                        .Select(payment => new { payment.PaymentRate, payment.PaymentMethodId })
                        .FirstOrDefaultAsync(cancellationToken);

                    decimal resolvedRefundRate;
                    int? resolvedRefundMethodId;
                    if (latestAllocation != null)
                    {
                        resolvedRefundRate = latestAllocation.PaymentRate;
                        resolvedRefundMethodId = latestAllocation.PaymentMethodId;
                    }
                    else
                    {
                        // hasReceivedMoney garantiza al menos un SalePayment cuando no hay abonos.
                        var latestSalePayment = sale.Payments
                            .OrderByDescending(payment => payment.CreatedAt)
                            .ThenByDescending(payment => payment.Id)
                            .First();
                        resolvedRefundRate = latestSalePayment.ExchangeRate;
                        resolvedRefundMethodId = latestSalePayment.PaymentMethodId;
                    }

                    refundedUsd = overpayUsd;
                    refundRate = resolvedRefundRate;
                    refundedBsS = Math.Round(overpayUsd * resolvedRefundRate, 2, MidpointRounding.AwayFromZero);

                    // El reembolso se asienta como salida física y toma el lock de la gaveta DESPUÉS
                    // del lock por cliente (mismo orden que el cierre de venta: cliente -> sesión).
                    var activeSession = await _cashDrawerService.GetOrCreateActiveSessionAsync(resolvedRefundRate, cancellationToken);
                    // 8.202-H1 (S2): el helper revalida Open bajo el mismo lock de la gaveta
                    // (el orden cartera -> sesión ya se respetó con el lock de cliente).
                    await _cashDrawerService.LockOpenSessionForWriteAsync(activeSession.Id, cancellationToken);

                    // La salida es física. Si el último pago fue efectivo se devuelve por la MISMA
                    // gaveta (USD o la moneda local del perfil) y se valida su liquidez; si fue
                    // no-cash, se enruta a la primera gaveta local del perfil. Nunca se asienta un
                    // egreso físico sobre un método electrónico.
                    (int refundMethodId, PettyCashCurrency refundCurrency, string refundCurrencyCode) =
                        await ResolveRefundCashRouteAsync(resolvedRefundMethodId, cancellationToken);

                    decimal refundRequiredAmount = refundCurrency == PettyCashCurrency.Usd
                        ? refundedUsd
                        : refundedBsS;

                    decimal availableInDrawer = await _cashDrawerService.GetPhysicalBalanceAsync(
                        activeSession.Id,
                        refundCurrency,
                        cancellationToken);
                    if (availableInDrawer < refundRequiredAmount)
                    {
                        throw new InvalidOperationException(
                            $"Saldo de efectivo en {refundCurrencyCode} insuficiente en la gaveta para reembolsar la devolución de la venta #{sale.Id}. Reembolso requerido: {MoneyFormat.N2(refundRequiredAmount)} {refundCurrencyCode}.");
                    }

                    // Salida de caja por el excedente efectivamente pagado: mismo alta automática de
                    // sesión/carry-over del cobro, tipo Expense y origen CreditRefund.
                    refundCashTransaction = new CashTransaction
                    {
                        SessionId = activeSession.Id,
                        Type = CashTransactionType.Expense,
                        Source = CashTransactionSource.CreditRefund,
                        AmountUsd = refundedUsd,
                        ExchangeRate = resolvedRefundRate,
                        AmountLocal = refundedBsS,
                        IsPhysicalCash = true,
                        Description = $"Reembolso devolución venta N° {sale.InvoiceNumber} ({sale.Customer?.Name ?? sale.CustomerName ?? $"Cliente #{receivable.CustomerId}"})",
                        TransactionTime = DateTime.UtcNow,
                        SaleId = sale.Id,
                        PaymentMethodId = refundMethodId,
                        // 8.211-T3c4 (S6/S7): la salida fisica snapshot el codigo real de la
                        // gaveta enrutada (COP/EUR/Bs.S), nunca un hardcode Bs.S.
                        CurrencyCode = refundCurrencyCode
                    };
                    _context.CashTransactions.Add(refundCashTransaction);
                }

                receivable.BalanceUSD = newBalance;
                if (newBalance == 0m)
                {
                    receivable.Status = ReceivableStatus.Anulada;

                    // Devolución total sin dinero recibido: no hay pago que reembolsar, la venta
                    // también se anula (con pago inicial o abonos la factura se conserva: ya hubo
                    // cobro y su ingreso debe permanecer neteándose en el cierre Z).
                    if (!hasReceivedMoney)
                    {
                        sale.Status = SaleStatus.Cancelled;
                    }
                }
                else if (hasPayments)
                {
                    receivable.Status = ReceivableStatus.Parcial;
                }
                else
                {
                    receivable.Status = CreditService.IsOverdueBeyondGrace(
                        receivable.DueDateUtc,
                        sale.Customer?.CreditGraceDays ?? 0,
                        TimeZoneHelper.GetVenezuelaDate())
                        ? ReceivableStatus.Vencida
                        : ReceivableStatus.Vigente;
                }

                var creditReturn = new CreditReturn
                {
                    SaleId = sale.Id,
                    ReceivableId = receivable.Id,
                    CustomerId = receivable.CustomerId,
                    ReturnedUSD = returnedUsd,
                    RefundedUSD = refundedUsd,
                    RefundedBsS = refundedBsS,
                    RefundRate = refundRate,
                    CashTransaction = refundCashTransaction,
                    CreatedAtUtc = DateTime.UtcNow,
                    Reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim(),
                    CreatedByUserId = actingUserId,
                    IdempotencyKey = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim()
                };
                foreach (var returnItem in returnItems)
                {
                    creditReturn.Items.Add(returnItem);
                }
                _context.CreditReturns.Add(creditReturn);

                await RestockCreditReturnItemsAsync(sale, returnItems, actingUserId, cancellationToken);

                await _context.SaveChangesAsync(cancellationToken);
                if (transaction != null)
                {
                    await transaction.CommitAsync(cancellationToken);
                    if (_inventoryService != null)
                    {
                        await _inventoryService.DetachFromTransactionAsync(cancellationToken);
                    }
                }

                _logger?.LogInformation(
                    "[SalesService] Devolución CxC venta #{SaleId}: ${ReturnedUsd} devueltos, saldo ${Balance} ({Status}) y reembolso ${RefundedUsd} ({RefundedLocal} local a tasa {RefundRate}).",
                    sale.Id, returnedUsd, newBalance, receivable.Status, refundedUsd, refundedBsS, refundRate);

                return MapCreditReturnResult(creditReturn, sale, receivable, isReplay: false);
            }
            catch
            {
                if (transaction != null)
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    if (_inventoryService != null)
                    {
                        await _inventoryService.DetachFromTransactionAsync(CancellationToken.None);
                    }
                }
                throw;
            }
        });
    }

    /// <summary>
    /// Replay idempotente de la devolución: una clave ya consumida devuelve el registro existente
    /// tal como quedó persistido (sin nueva caja, stock ni saldo). Una clave reutilizada por otra
    /// venta es un conflicto explícito (el índice único filtrado es la defensa final).
    /// </summary>
    private async Task<CreditReturnResultDto?> FindExistingCreditReturnAsync(
        string? idempotencyKey,
        int saleId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
        {
            return null;
        }

        string key = idempotencyKey.Trim();
        var existing = await _context.CreditReturns
            .AsNoTracking()
            .AsSplitQuery()
            .Include(creditReturn => creditReturn.Items)
            .Include(creditReturn => creditReturn.Sale)
            .Include(creditReturn => creditReturn.Receivable)
            .FirstOrDefaultAsync(creditReturn => creditReturn.IdempotencyKey == key, cancellationToken);

        if (existing == null)
        {
            return null;
        }

        if (existing.SaleId != saleId)
        {
            throw new InvalidOperationException($"La clave de idempotencia '{key}' ya fue utilizada en una devolución de otra venta.");
        }

        return MapCreditReturnResult(existing, existing.Sale, existing.Receivable, isReplay: true);
    }

    /// <summary>
    /// Gaveta física del reembolso según el perfil activo. Si el último pago fue efectivo se
    /// conserva su método y su moneda cuando esa moneda pertenece al perfil (USD cash solo si USD
    /// está en el perfil); una gaveta de una moneda ajena al perfil se ignora. Si fue no-cash o no
    /// existe, se enruta al primer método cash activo de la moneda local del perfil (COP en Dual
    /// USD/COP) y la salida se valida contra esa gaveta. El código devuelto es el real de la
    /// gaveta y gobierna el snapshot, el mensaje de liquidez y la etiqueta del monto.
    /// </summary>
    private async Task<(int MethodId, PettyCashCurrency Currency, string CurrencyCode)> ResolveRefundCashRouteAsync(
        int? lastPaymentMethodId,
        CancellationToken cancellationToken)
    {
        // 8.211-T3c4 (S7): physical refund routing must not degrade a non-VES profile to Bs.S.
        var monetaryProfile = await ResolveMonetaryProfileSnapshotAsync(cancellationToken, failClosed: true);
        string localCurrency = ResolveLocalDrawerCurrency(monetaryProfile);
        bool includesUsd = IsCurrencyInProfile(monetaryProfile, PaymentMethodCurrencyResolver.Usd);

        var cashMethods = await _context.PaymentMethods
            .AsNoTracking()
            .Where(method => method.IsCash && method.IsActive && !method.IsDeleted)
            .OrderBy(method => method.Id)
            .Select(method => new { method.Id, method.Name, method.Currency })
            .ToListAsync(cancellationToken);

        if (lastPaymentMethodId is int lastMethodId)
        {
            var lastPaymentMethod = cashMethods.FirstOrDefault(method => method.Id == lastMethodId);
            if (lastPaymentMethod is not null)
            {
                string methodCurrency = PaymentMethodCurrencyResolver.ResolveStoredOrName(
                    lastPaymentMethod.Currency,
                    lastPaymentMethod.Name);

                if (MonetaryRateResolver.IsSameCurrency(methodCurrency, PaymentMethodCurrencyResolver.Usd))
                {
                    if (includesUsd)
                    {
                        return (lastPaymentMethod.Id, PettyCashCurrency.Usd, PaymentMethodCurrencyResolver.Usd);
                    }
                }
                else if (IsCurrencyInProfile(monetaryProfile, methodCurrency))
                {
                    return (lastPaymentMethod.Id, PettyCashCurrency.BsS, methodCurrency);
                }
            }
        }

        var localCashMethod = cashMethods.FirstOrDefault(method => MonetaryRateResolver.IsSameCurrency(
            PaymentMethodCurrencyResolver.ResolveStoredOrName(method.Currency, method.Name),
            localCurrency));

        if (localCashMethod is null)
        {
            throw new InvalidOperationException(
                $"No existe un método de pago de efectivo en {localCurrency} configurado para asentar el reembolso de la devolución.");
        }

        return (localCashMethod.Id, PettyCashCurrency.BsS, localCurrency);
    }

    /// <summary>
    /// Indica si el código pertenece al perfil (primaria o secundaria). En Single la secundaria es
    /// NULL, de modo que solo la primaria habilita una gaveta.
    /// </summary>
    private static bool IsCurrencyInProfile(MonetaryProfileSnapshot profile, string currencyCode)
        => MonetaryRateResolver.IsSameCurrency(currencyCode, profile.PrimaryCurrencyCode)
            || (profile.SecondaryCurrencyCode is not null
                && MonetaryRateResolver.IsSameCurrency(currencyCode, profile.SecondaryCurrencyCode));

    private static CreditReturnResultDto MapCreditReturnResult(
        CreditReturn creditReturn,
        Sale? sale,
        Receivable? receivable,
        bool isReplay)
    {
        return new CreditReturnResultDto
        {
            ReturnId = creditReturn.Id,
            SaleId = creditReturn.SaleId,
            ReceivableId = creditReturn.ReceivableId,
            CustomerId = creditReturn.CustomerId,
            ReturnedUSD = creditReturn.ReturnedUSD,
            RefundedUSD = creditReturn.RefundedUSD,
            RefundedBsS = creditReturn.RefundedBsS,
            RefundRate = creditReturn.RefundRate,
            CashTransactionId = creditReturn.CashTransactionId,
            BalanceAfterUSD = receivable?.BalanceUSD ?? 0m,
            Status = receivable?.Status ?? ReceivableStatus.Anulada,
            SaleStatus = sale?.Status ?? SaleStatus.Cancelled,
            CreatedAtUtc = creditReturn.CreatedAtUtc,
            Reason = creditReturn.Reason,
            IsReplay = isReplay,
            Items = creditReturn.Items.Select(item => new CreditReturnItemResultDto
            {
                SaleItemId = item.SaleItemId,
                Quantity = item.Quantity,
                AmountUSD = item.AmountUSD
            }).ToList()
        };
    }

    private static void ValidateCreditReturnRequest(CreditReturnRequest request)
    {
        if (request.Items == null || request.Items.Count == 0)
        {
            throw new ArgumentException("La devolución debe incluir al menos un ítem de la venta.");
        }

        if (!string.IsNullOrWhiteSpace(request.Reason) && request.Reason.Trim().Length > 500)
        {
            throw new ArgumentException("El motivo de la devolución excede la longitud máxima permitida (500 caracteres).");
        }

        if (!string.IsNullOrWhiteSpace(request.IdempotencyKey) && request.IdempotencyKey.Trim().Length > 100)
        {
            throw new ArgumentException("La clave de idempotencia de la devolución excede la longitud máxima permitida (100 caracteres).");
        }

        var duplicateItemIds = request.Items
            .Where(item => item != null)
            .GroupBy(item => item.SaleItemId)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();
        if (duplicateItemIds.Count > 0)
        {
            throw new ArgumentException("El detalle de la devolución contiene ítems repetidos de la venta; consolide las cantidades en un único registro por ítem.");
        }

        foreach (var item in request.Items)
        {
            if (item == null || item.SaleItemId <= 0)
            {
                throw new ArgumentException("El detalle de la devolución contiene un ítem inválido.");
            }

            if (item.Quantity <= 0m)
            {
                throw new ArgumentException("La cantidad devuelta debe ser mayor a cero.");
            }
        }
    }

    /// <summary>
    /// Restituye TODO el stock de la venta anulada (motivo Return) por el mismo camino de
    /// inventario del cierre. Los servicios de adelanto de efectivo no manejan inventario físico
    /// y se excluyen, igual que en la deducción del checkout.
    /// </summary>
    private async Task RestockCreditSaleItemsAsync(Sale sale, int? actingUserId, CancellationToken cancellationToken)
    {
        if (_inventoryService == null || sale.Items == null || sale.Items.Count == 0)
        {
            return;
        }

        var productsDict = await LoadSaleProductsDictionaryAsync(
            sale.Items.Select(item => item.ProductId),
            cancellationToken);

        string reasonLabel = $"Anulación venta a crédito N° {sale.InvoiceNumber}";
        var requests = new List<StockDeductionRequest>();
        foreach (var saleItem in sale.Items)
        {
            if (productsDict.TryGetValue(saleItem.ProductId, out var product) && product.IsCashAdvance)
            {
                continue;
            }

            if (saleItem.Quantity <= 0m)
            {
                continue;
            }

            requests.Add(new StockDeductionRequest(
                saleItem.ProductId,
                saleItem.Quantity,
                reasonLabel,
                sale.Id,
                StockMovementOriginType.System,
                StockMovementReason.Return));
        }

        if (requests.Count > 0)
        {
            await _inventoryService.UpdateStockBatchAsync(
                requests,
                userId: actingUserId?.ToString(),
                allowNegativeStock: false,
                cancellationToken: cancellationToken);
        }
    }

    /// <summary>
    /// Restituye EXCLUSIVAMENTE las cantidades devueltas (motivo Return). La suma de líneas
    /// homólogas se agrupa por ítem para emitir un único movimiento por producto.
    /// </summary>
    private async Task RestockCreditReturnItemsAsync(
        Sale sale,
        IReadOnlyCollection<CreditReturnItem> returnItems,
        int? actingUserId,
        CancellationToken cancellationToken)
    {
        if (_inventoryService == null || returnItems.Count == 0)
        {
            return;
        }

        var returnedBySaleItemId = returnItems
            .GroupBy(item => item.SaleItemId)
            .ToDictionary(group => group.Key, group => group.Sum(item => item.Quantity));

        var saleItems = sale.Items
            .Where(item => returnedBySaleItemId.ContainsKey(item.Id))
            .ToList();
        var productsDict = await LoadSaleProductsDictionaryAsync(
            saleItems.Select(item => item.ProductId),
            cancellationToken);

        string reasonLabel = $"Devolución venta a crédito N° {sale.InvoiceNumber}";
        var requests = new List<StockDeductionRequest>();
        foreach (var saleItem in saleItems)
        {
            if (productsDict.TryGetValue(saleItem.ProductId, out var product) && product.IsCashAdvance)
            {
                continue;
            }

            decimal quantity = returnedBySaleItemId[saleItem.Id];
            if (quantity <= 0m)
            {
                continue;
            }

            requests.Add(new StockDeductionRequest(
                saleItem.ProductId,
                quantity,
                reasonLabel,
                sale.Id,
                StockMovementOriginType.System,
                StockMovementReason.Return));
        }

        if (requests.Count > 0)
        {
            await _inventoryService.UpdateStockBatchAsync(
                requests,
                userId: actingUserId?.ToString(),
                allowNegativeStock: false,
                cancellationToken: cancellationToken);
        }
    }

    private async Task<Dictionary<int, SaleProductInfoDto>> LoadSaleProductsDictionaryAsync(
        IEnumerable<int> productIds,
        CancellationToken cancellationToken)
    {
        var ids = productIds.Distinct().ToList();
        if (_inventoryService == null || ids.Count == 0)
        {
            return new Dictionary<int, SaleProductInfoDto>();
        }

        var products = await _inventoryService.GetSaleProductsByIdsAsync(ids, cancellationToken);
        return products == null
            ? new Dictionary<int, SaleProductInfoDto>()
            : products.ToDictionary(product => product.Id);
    }
}
