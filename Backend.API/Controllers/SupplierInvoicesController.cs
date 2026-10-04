using Core.DTOs;
using Core.Interfaces;
using Inventory.Module.Services.Ocr;
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
    private readonly IOcrExtractionService _ocrExtractionService;

    public SupplierInvoicesController(
        ISupplierInvoiceService supplierInvoiceService,
        IOcrExtractionService ocrExtractionService)
    {
        ArgumentNullException.ThrowIfNull(supplierInvoiceService);
        ArgumentNullException.ThrowIfNull(ocrExtractionService);
        _supplierInvoiceService = supplierInvoiceService;
        _ocrExtractionService = ocrExtractionService;
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

    [HttpPost("ocr-extract")]
    [ProducesResponseType(typeof(OcrExtractionResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<OcrExtractionResultDto>> OcrExtractAsync(
        [FromForm] IFormFile? file,
        [FromForm] int? supplierId,
        [FromForm] string? nameColumn,
        [FromForm] string? quantityColumn,
        [FromForm] string? unitCostColumn,
        [FromForm] string? supplierCodeColumn,
        [FromForm] string? barcodeColumn,
        CancellationToken cancellationToken)
    {
        // 8.147-T4/D12: guardas rápidas (archivo, tipo y tamaño) antes de tocar el pipeline OCR;
        // los mismos límites vuelven a validarse en OcrExtractionService (zero-trust).
        if (file is null)
        {
            return this.ApiBadRequest("Solicitud de extracción OCR inválida.", "El archivo es obligatorio.");
        }

        if (file.Length == 0)
        {
            return this.ApiBadRequest("Solicitud de extracción OCR inválida.", "El archivo está vacío.");
        }

        var extension = Path.GetExtension(file.FileName);
        if (!OcrExtractionService.AllowedExtensions.Contains(extension.ToLowerInvariant()))
        {
            return this.ApiBadRequest(
                "Solicitud de extracción OCR inválida.",
                OcrExtractionService.UnsupportedExtensionMessage(extension));
        }

        if (file.Length > OcrExtractionService.MaxFileBytes)
        {
            return this.ApiBadRequest(
                "Solicitud de extracción OCR inválida.",
                $"El archivo supera el tamaño máximo permitido de {OcrExtractionService.MaxFileBytes / (1024 * 1024)} MB.");
        }

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer, cancellationToken);

        // El mapping solo se arma cuando el cliente envía las tres columnas obligatorias de la
        // plantilla; si falta alguna, se usa el fallback genérico del parser (D7).
        var columnMapping = string.IsNullOrWhiteSpace(nameColumn)
            || string.IsNullOrWhiteSpace(quantityColumn)
            || string.IsNullOrWhiteSpace(unitCostColumn)
            ? null
            : new SupplierColumnMappingDto(barcodeColumn, supplierCodeColumn, nameColumn, quantityColumn, unitCostColumn);

        var result = await _ocrExtractionService.ExtractAsync(
            new OcrExtractionRequestDto(buffer.ToArray(), file.FileName, supplierId, columnMapping),
            cancellationToken);

        return Ok(result);
    }
}
