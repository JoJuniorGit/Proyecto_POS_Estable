using System.Text.Json;
using Backend.API.Attributes;
using Core.DTOs;
using Core.Interfaces;
using Inventory.Module.Services.Ocr;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

[ApiController]
[Authorize(Roles = "Admin,Manager")]
[RequireSecurityStampValidation]
[Route("api/supplier-invoices")]
public sealed class SupplierInvoicesController : ControllerBase
{
    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);

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

    [HttpPost("{id:int}/lines/{lineId:int}/link-product")]
    [ProducesResponseType(typeof(SupplierInvoiceDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<SupplierInvoiceDetailDto>> LinkProductToLineAsync(
        int id,
        int lineId,
        [FromBody] LinkInvoiceProductRequestDto request,
        CancellationToken cancellationToken)
    {
        var invoice = await _supplierInvoiceService.LinkProductToLineAsync(id, lineId, request, cancellationToken);
        return Ok(invoice);
    }

    [HttpGet("suppliers/{supplierId:int}/ocr-template")]
    [ProducesResponseType(typeof(SupplierOcrTemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult<SupplierOcrTemplateDto>> GetSupplierOcrTemplateAsync(
        int supplierId,
        CancellationToken cancellationToken)
    {
        var template = await _supplierInvoiceService.GetSupplierOcrTemplateAsync(supplierId, cancellationToken);
        if (template is null)
        {
            return NoContent();
        }

        return Ok(template);
    }

    [HttpPut("suppliers/{supplierId:int}/ocr-template")]
    [ProducesResponseType(typeof(SupplierOcrTemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SupplierOcrTemplateDto>> SaveSupplierOcrTemplateAsync(
        int supplierId,
        [FromBody] SupplierOcrTemplateDto? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
        {
            return this.ApiBadRequest("Plantilla OCR inválida.", "El cuerpo de la plantilla OCR es obligatorio.");
        }

        try
        {
            var saved = await _supplierInvoiceService.SaveSupplierOcrTemplateAsync(supplierId, request, cancellationToken);
            return Ok(saved);
        }
        catch (KeyNotFoundException ex)
        {
            return Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Supplier not found.",
                detail: ex.Message);
        }
    }

    [HttpPost("ocr-extract")]
    [RequestSizeLimit(OcrExtractionService.MaxFileBytes + 5L * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = OcrExtractionService.MaxFileBytes + 4L * 1024 * 1024)]
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
        [FromForm] string? ocrTemplateJson = null,
        CancellationToken cancellationToken = default)
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

        SupplierOcrTemplateDto? ocrTemplate = null;
        if (!string.IsNullOrWhiteSpace(ocrTemplateJson))
        {
            try
            {
                ocrTemplate = JsonSerializer.Deserialize<SupplierOcrTemplateDto>(ocrTemplateJson, WebJsonOptions);
            }
            catch (JsonException)
            {
                return this.ApiBadRequest(
                    "Solicitud de extracción OCR inválida.",
                    "El formato JSON de la plantilla OCR es inválido.");
            }
        }
        else if (supplierId.HasValue)
        {
            ocrTemplate = await _supplierInvoiceService.GetSupplierOcrTemplateAsync(supplierId.Value, cancellationToken);
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
            new OcrExtractionRequestDto(buffer.ToArray(), file.FileName, supplierId, columnMapping, ocrTemplate),
            cancellationToken);

        return Ok(result);
    }
}
