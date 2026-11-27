using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Services;

namespace Sales.Module.Receipts;

public sealed record SaleReceiptData(SaleReceiptContext Context, int? CashierId, bool HasInvoice);

/// <summary>
/// T3a (F2b, S5): comprobante de abono CxC + receptor del cobro. El cajero solo puede descargar
/// sus propios comprobantes (ReceivedByUserId) salvo elevación Admin/Manager en el controlador.
/// </summary>
public sealed record CreditPaymentReceiptData(CreditPaymentReceiptContext Context, int? ReceivedByUserId);

/// <summary>
/// T3b1 (F2b, S5): comprobante de venta a crédito + cajero firmante de la venta. Venta sin
/// obligación CxC asociada ⇒ <see cref="HasReceivable"/> false y <see cref="Context"/> null
/// (el controlador responde 400); venta inexistente ⇒ el servicio devuelve null (404).
/// </summary>
public sealed record CreditSaleReceiptData(CreditSaleReceiptContext? Context, int? CashierId, bool HasReceivable);

public interface ISalesReceiptService
{
    Task<SaleReceiptData?> GetReceiptDataAsync(int saleId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Carga el abono con imputaciones, cliente, método de pago y saldo pendiente vigente del
    /// cliente para armar el comprobante de abono. <paramref name="todayRate"/> es la tasa efectiva
    /// del día (≤ 0 ⇒ el contexto conserva la tasa del abono para la proyección Bs.S).
    /// </summary>
    Task<CreditPaymentReceiptData?> GetCreditPaymentReceiptDataAsync(int paymentId, decimal todayRate, CancellationToken cancellationToken = default);

    /// <summary>
    /// T3b1 (F2b, S5): carga la venta completada (ítems, pagos, snapshot de cliente) junto con su
    /// obligación CxC para armar el comprobante de venta a crédito. Venta inexistente → null.
    /// </summary>
    Task<CreditSaleReceiptData?> GetCreditSaleReceiptDataAsync(int saleId, CancellationToken cancellationToken = default);
}

public class SalesReceiptService : ISalesReceiptService
{
    private readonly SalesDbContext _db;
    // 8.211-T4a (S10/F4a): el perfil aporta el símbolo configurado de la moneda persistida. Es
    // opcional para que las composiciones directas de tests sigan compilando; sin él rigen los
    // defaults ASCII (USD/$, VES/Bs.S, otros su código ISO).
    private readonly IMonetaryProfileService? _monetaryProfileService;

    public SalesReceiptService(SalesDbContext db, IMonetaryProfileService? monetaryProfileService = null)
    {
        _db = db;
        _monetaryProfileService = monetaryProfileService;
    }

    private async Task<MonetaryProfileDto?> GetProfileAsync(CancellationToken cancellationToken)
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
            // Un comprobante histórico no debe quedar sin imprimir por una lectura de settings;
            // los códigos persistidos siguen siendo la autoridad y las etiquetas caen a sus defaults.
            Core.Logging.AppLogger.LogWarn(
                $"[SalesReceiptService] No se pudo leer el perfil monetario ({ex.Message}); se usan las etiquetas por código.",
                nameof(SalesReceiptService));
            return null;
        }
    }

    public async Task<SaleReceiptData?> GetReceiptDataAsync(int saleId, CancellationToken cancellationToken = default)
    {
        var sale = await _db.Sales
            .AsNoTracking()
            .AsSplitQuery()
            .Include(s => s.Items)
            .Include(s => s.Payments).ThenInclude(p => p.PaymentMethod)
            .FirstOrDefaultAsync(s => s.Id == saleId, cancellationToken);

        if (sale == null) return null;
        var profile = await GetProfileAsync(cancellationToken);
        return new SaleReceiptData(SaleReceiptContext.CreateFrom(sale, profile), sale.CashierId, sale.InvoiceNumber.HasValue);
    }

    public async Task<CreditPaymentReceiptData?> GetCreditPaymentReceiptDataAsync(int paymentId, decimal todayRate, CancellationToken cancellationToken = default)
    {
        var payment = await _db.ReceivablePayments
            .AsNoTracking()
            .AsSplitQuery()
            .Include(p => p.Allocations).ThenInclude(a => a.Receivable).ThenInclude(r => r.Sale)
            .Include(p => p.PaymentMethod)
            .Include(p => p.Customer)
            .FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);

        if (payment == null) return null;

        var receivableIds = payment.Allocations.Select(allocation => allocation.ReceivableId).Distinct().ToList();

        // Imputaciones posteriores por obligación (orden estricto ReceivedAtUtc, Id): permiten
        // reconstruir el saldo que quedó tras ESTE abono aunque el saldo corriente ya haya cambiado.
        var laterAllocations = await _db.CreditPaymentAllocations
            .AsNoTracking()
            .Where(allocation => receivableIds.Contains(allocation.ReceivableId)
                && (allocation.ReceivablePayment.ReceivedAtUtc > payment.ReceivedAtUtc
                    || (allocation.ReceivablePayment.ReceivedAtUtc == payment.ReceivedAtUtc
                        && allocation.ReceivablePaymentId > payment.Id)))
            .GroupBy(allocation => allocation.ReceivableId)
            .Select(group => new { ReceivableId = group.Key, AmountUSD = group.Sum(allocation => allocation.AmountUSD) })
            .ToDictionaryAsync(entry => entry.ReceivableId, entry => entry.AmountUSD, cancellationToken);

        var openStatuses = new[] { ReceivableStatus.Vigente, ReceivableStatus.Parcial, ReceivableStatus.Vencida };
        decimal currentDebtUsd = await _db.Receivables
            .AsNoTracking()
            .Where(receivable => receivable.CustomerId == payment.CustomerId
                && openStatuses.Contains(receivable.Status)
                && receivable.BalanceUSD > CreditService.BalanceToleranceUsd)
            .SumAsync(receivable => receivable.BalanceUSD, cancellationToken);

        var profile = await GetProfileAsync(cancellationToken);
        var context = CreditPaymentReceiptContext.CreateFrom(payment, currentDebtUsd, todayRate, laterAllocations, profile);
        return new CreditPaymentReceiptData(context, payment.ReceivedByUserId);
    }

    public async Task<CreditSaleReceiptData?> GetCreditSaleReceiptDataAsync(int saleId, CancellationToken cancellationToken = default)
    {
        var sale = await _db.Sales
            .AsNoTracking()
            .AsSplitQuery()
            .Include(s => s.Items)
            .Include(s => s.Payments).ThenInclude(p => p.PaymentMethod)
            .FirstOrDefaultAsync(s => s.Id == saleId, cancellationToken);

        if (sale == null) return null;

        // T3b1 (S5): el comprobante solo existe para ventas con obligación CxC (factura a crédito);
        // una venta de contado completada no tiene documento de entrega a crédito.
        var receivable = await _db.Receivables
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.SaleId == saleId, cancellationToken);

        if (receivable == null)
        {
            return new CreditSaleReceiptData(null, sale.CashierId, HasReceivable: false);
        }

        var profile = await GetProfileAsync(cancellationToken);
        return new CreditSaleReceiptData(
            CreditSaleReceiptContext.CreateFrom(sale, receivable, profile),
            sale.CashierId,
            HasReceivable: true);
    }
}
