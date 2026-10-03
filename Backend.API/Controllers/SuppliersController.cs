using System.Collections.Generic;
using Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

[ApiController]
[Authorize(Roles = "Admin,Manager")]
[Route("api/suppliers")]
public sealed class SuppliersController : ControllerBase
{
    private readonly ISupplierInvoiceService _supplierInvoiceService;

    public SuppliersController(ISupplierInvoiceService supplierInvoiceService)
    {
        ArgumentNullException.ThrowIfNull(supplierInvoiceService);
        _supplierInvoiceService = supplierInvoiceService;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<SupplierSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<IReadOnlyList<SupplierSummaryDto>>> GetAsync(
        [FromQuery(Name = "rif")] string? rifOrNit,
        [FromQuery] string? commercialName,
        CancellationToken cancellationToken)
    {
        var suppliers = await _supplierInvoiceService.GetSuppliersAsync(
            rifOrNit,
            commercialName,
            cancellationToken);
        return Ok(suppliers);
    }

    [HttpPost]
    [ProducesResponseType(typeof(SupplierSummaryDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<SupplierSummaryDto>> CreateAsync(
        [FromBody] CreateSupplierRequestDto request,
        CancellationToken cancellationToken)
    {
        var supplier = await _supplierInvoiceService.CreateSupplierAsync(request, cancellationToken);
        var routeValues = new Dictionary<string, object?>
        {
            ["rif"] = supplier.RifOrNit,
            ["commercialName"] = supplier.RifOrNit is null ? supplier.CommercialName : null
        };

        return CreatedAtAction(nameof(GetAsync), routeValues, supplier);
    }
}
