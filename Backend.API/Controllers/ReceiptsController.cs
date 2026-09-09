using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Receipts;

namespace Backend.API.Controllers;

[Authorize]
[ApiController]
[Route("api/sales")]
public class ReceiptsController : ControllerBase
{
    private readonly SalesDbContext _salesDb;
    private readonly IReceiptDocumentRenderer _renderer;

    public ReceiptsController(SalesDbContext salesDb, IReceiptDocumentRenderer renderer)
    {
        _salesDb = salesDb;
        _renderer = renderer;
    }

    [HttpGet("{saleId:int}/receipt")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<IActionResult> GetReceipt(int saleId, CancellationToken cancellationToken)
    {
        var sale = await _salesDb.Sales
            .AsNoTracking()
            .AsSplitQuery()
            .Include(s => s.Items)
            .Include(s => s.Payments).ThenInclude(p => p.PaymentMethod)
            .FirstOrDefaultAsync(s => s.Id == saleId, cancellationToken);

        if (sale == null)
        {
            return NotFound();
        }

        if (!sale.InvoiceNumber.HasValue)
        {
            return this.ApiBadRequest("La venta aún no está completada y no tiene recibo.");
        }

        var document = _renderer.Render(SaleReceiptContext.CreateFrom(sale));
        return File(document.Bytes!, "application/pdf", document.FileName);
    }
}