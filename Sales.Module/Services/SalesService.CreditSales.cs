using Core.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

public partial class SalesService
{
    /// <summary>
    /// 8.187-T2 (F2a, S2/S6): cierre transaccional de venta a crédito (total o mixta con abono
    /// inicial). Delega en el núcleo compartido del checkout con <c>allowFinancedRemainder</c>:
    /// lo cobrado genera pagos/caja como en el cierre regular y el remanente efectivo en Bs.S por
    /// encima del polvo decimal se financia como Cuenta por Cobrar. La factura se emite al
    /// completar, el stock se descuenta de inmediato y el monto financiado no ingresa a la gaveta.
    /// </summary>
    public async Task<int> CompleteCreditSaleAsync(
        int saleId,
        CreditSaleRequest request,
        int? actingUserId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return await CompleteSaleCoreAsync(
            saleId,
            request.ExchangeRate,
            MapCreditSalePayments(request.Payments),
            roundingAdjustment: 0m,
            cashierId: request.CashierId,
            isPendingPickup: false,
            idempotencyKey: null,
            idempotencyPayloadHash: null,
            cancellationToken,
            actingUserId,
            allowFinancedRemainder: true,
            idempotentRequestPath: null);
    }

    /// <summary>
    /// Espejo del mapeo del cierre regular de contado: el monto Bs.S explícito tiene prioridad
    /// sobre el local y los pagos nulos se descartan. Así el abono inicial atraviesa exactamente
    /// el mismo camino de validación/persistencia/caja que un pago de contado.
    /// </summary>
    private static IEnumerable<PaymentInfo> MapCreditSalePayments(IEnumerable<SalePaymentDto>? payments)
    {
        return payments?
            .Where(payment => payment != null)
            .Select(payment => new PaymentInfo(
                payment.PaymentMethodId,
                payment.Amount,
                payment.AmountBsS > 0m ? payment.AmountBsS : payment.AmountLocal,
                payment.ReferenceNumber))
            ?? Enumerable.Empty<PaymentInfo>();
    }

    /// <summary>
    /// 8.187-T2 (F2a, S2/S6): evalúa solvencia en tiempo real y constituye la obligación CxC del
    /// remanente financiado. La deuda se denomina en USD 4dp derivada del Bs.S EFECTIVO financiado
    /// sobre la tasa de emisión anclada (<see cref="Sale.AppliedRate"/>), que queda congelada como
    /// <see cref="Receivable.HistoricalRate"/>; sin caja ni <see cref="SalePayment"/> por lo
    /// financiado. Corre dentro de la transacción y bajo el lock por cliente del núcleo de cierre.
    /// </summary>
    private async Task CreateCreditReceivableAsync(
        Sale sale,
        decimal financedBsS,
        decimal financedUsd,
        CancellationToken cancellationToken)
    {
        // Fail-closed: sin el servicio de crédito no hay evaluación de solvencia posible.
        if (_creditService == null)
        {
            throw new InvalidOperationException("El servicio de crédito no está configurado; no es posible financiar el remanente de la venta.");
        }

        int customerId = sale.CustomerId
            ?? throw new ArgumentException("Para registrar una venta a crédito se requiere seleccionar o crear un cliente registrado (Nombre, Cédula y Teléfono).");

        var eligibility = await _creditService.EvaluateEligibilityAsync(customerId, financedUsd, cancellationToken);
        if (!eligibility.IsEligible)
        {
            throw new InvalidOperationException(string.Join(" ", eligibility.Blockers));
        }

        // Defensivo (S5): la obligación es única por venta (IX_Receivables_SaleId). El reintento
        // idempotente se resuelve antes por el estado Completed; este guard convierte cualquier
        // intento de segunda obligación en un error de dominio claro y auditable.
        bool receivableExists = await _context.Receivables
            .AsNoTracking()
            .AnyAsync(receivable => receivable.SaleId == sale.Id, cancellationToken);
        if (receivableExists)
        {
            throw new InvalidOperationException($"La venta #{sale.Id} ya posee una cuenta por cobrar asociada; no se puede constituir una segunda obligación sobre la misma factura.");
        }

        // El plazo del cliente se snapshotea en la obligación: vencimiento = emisión + días
        // continuos configurados (S1). La elegibilidad ya garantiza un plazo mayor a cero.
        int paymentTermDays = await _context.Customers
            .AsNoTracking()
            .Where(customer => customer.Id == customerId)
            .Select(customer => customer.PaymentTermDays)
            .FirstAsync(cancellationToken);

        var issuedAtUtc = DateTime.UtcNow;
        _context.Receivables.Add(new Receivable
        {
            SaleId = sale.Id,
            CustomerId = customerId,
            OriginalUSD = financedUsd,
            BalanceUSD = financedUsd,
            OriginalBsS = financedBsS,
            HistoricalRate = sale.AppliedRate,
            IssuedAtUtc = issuedAtUtc,
            DueDateUtc = issuedAtUtc.AddDays(paymentTermDays),
            Status = ReceivableStatus.Vigente
        });

        _logger?.LogInformation(
            "[SalesService] Venta a crédito #{SaleId}: obligación CxC por ${FinancedUsd} (Bs.S {FinancedBsS}) a {PaymentTermDays} días continuos.",
            sale.Id, financedUsd, financedBsS, paymentTermDays);
    }
}
