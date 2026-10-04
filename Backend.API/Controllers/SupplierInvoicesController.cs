using Core.DTOs;
using Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

[ApiController]
[Authorize(Roles = "Admin,Manager")]
[Route("api/supplier-invoices")]
public sealed class SupplierInvoicesController : ControllerBase
{
    private readonly ISupplierInvoiceService _supplierInvoiceService;

    public SupplierInvoicesController(ISupplierInvoiceService supplierInvoiceService)
    {
        ArgumentNullException.ThrowIfNull(supplierInvoiceService);
        _supplierInvoiceService = supplierInvoiceService;
    }

    [HttpPost]
    [ProducesResponseType(typeof(SupplierInvoiceDetailDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SupplierInvoiceDetailDto>> StageAsync(
        [FromBody] StageSupplierInvoiceRequestDto request,
        CancellationToken cancellationToken)
    {
        var invoice = await _supplierInvoiceService.StageAsync(request, cancellationToken);
        return CreatedAtAction(nameof(GetAsync), new { id = invoice.Id }, invoice);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(SupplierInvoiceDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierInvoiceDetailDto>> GetAsync(
        int id,
        CancellationToken cancellationToken)
    {
        var invoice = await _supplierInvoiceService.GetInvoiceAsync(id, cancellationToken);
        if (invoice is null)
        {
            return Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Supplier invoice not found.",
                detail: $"Supplier invoice {id} was not found.");
        }

        return Ok(invoice);
    }

    [HttpPost("{id:int}/confirm")]
    [ProducesResponseType(typeof(SupplierInvoiceDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SupplierInvoiceDetailDto>> ConfirmAsync(
        int id,
        [FromBody] ConfirmSupplierInvoiceRequestDto request,
        CancellationToken cancellationToken)
    {
        var invoice = await _supplierInvoiceService.ConfirmAsync(id, request, cancellationToken);
        return Ok(invoice);
    }

    [HttpPost("{id:int}/lines/{lineId:int}/create-product")]
    [ProducesResponseType(typeof(SupplierInvoiceDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SupplierInvoiceDetailDto>> CreateProductFromLineAsync(
        int id,
        int lineId,
        [FromBody] CreateInvoiceProductRequestDto request,
        CancellationToken cancellationToken)
    {
        var invoice = await _supplierInvoiceService.CreateProductFromLineAsync(id, lineId, request, cancellationToken);
        return Ok(invoice);
    }
}
