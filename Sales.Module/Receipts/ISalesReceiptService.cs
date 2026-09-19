using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;

namespace Sales.Module.Receipts;

public sealed record SaleReceiptData(SaleReceiptContext Context, int? CashierId, bool HasInvoice);

public interface ISalesReceiptService
{
    Task<SaleReceiptData?> GetReceiptDataAsync(int saleId, CancellationToken cancellationToken = default);
}

public class SalesReceiptService : ISalesReceiptService
{
    private readonly SalesDbContext _db;

    public SalesReceiptService(SalesDbContext db)
    {
        _db = db;
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
        return new SaleReceiptData(SaleReceiptContext.CreateFrom(sale), sale.CashierId, sale.InvoiceNumber.HasValue);
    }
}
