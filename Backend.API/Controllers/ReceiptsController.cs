using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sales.Module.Data;
using Sales.Module.Receipts;

namespace Backend.API.Controllers;

[Authorize]
[ApiController]
[Route("api/sales")]
public class ReceiptsController : ControllerBase
{
    private readonly ISalesReceiptService _receiptService;
    private readonly IReceiptDocumentRenderer _renderer;
    private readonly Core.Interfaces.ICurrentUserService _currentUserService;

    public ReceiptsController(ISalesReceiptService receiptService, IReceiptDocumentRenderer renderer, Core.Interfaces.ICurrentUserService currentUserService)
    {
        _receiptService = receiptService;
        _renderer = renderer;
        _currentUserService = currentUserService;
    }

    [HttpGet("{saleId:int}/receipt")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<IActionResult> GetReceiptAsync(int saleId, CancellationToken cancellationToken = default)
    {
        var data = await _receiptService.GetReceiptDataAsync(saleId, cancellationToken);
        if (data == null)
        {
            return this.ApiNotFound("La venta solicitada no existe.");
        }

        var (context, cashierId, hasInvoice) = data;

        bool isElevated = User.IsInRole("Admin") || User.IsInRole("Manager");
        if (!isElevated)
        {
            if (string.IsNullOrEmpty(_currentUserService.UserId) || !int.TryParse(_currentUserService.UserId, out int uid) || cashierId != uid)
            {
                return this.ApiForbidden("Acceso denegado: no tiene permisos para descargar el recibo de esta venta.");
            }
        }

        if (!hasInvoice)
        {
            return this.ApiBadRequest("La venta aún no está completada y no tiene recibo.");
        }

        if (context == null)
        {
            return this.ApiNotFound("No se pudo generar el contexto de comprobante.");
        }

        var document = _renderer.Render(context);
        return File(document.Bytes!, "application/pdf", document.FileName);
    }

    [NonAction]
    public Task<IActionResult> GetReceipt(int saleId, CancellationToken cancellationToken) => GetReceiptAsync(saleId, cancellationToken);
}