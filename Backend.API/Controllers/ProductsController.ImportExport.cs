using Core.Entities;
using Core.DTOs;
using Core.Interfaces;
using Backend.API.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

public partial class ProductsController
{

    [HttpPost("bulk-import")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> BulkImport([FromBody] Core.DTOs.BulkImportRequestDto request, System.Threading.CancellationToken cancellationToken)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "El rol Cajero no tiene permisos para realizar importaciones.");
        }
        // 8I-M3: tope de lote en el endpoint (la validación fina por fila ocurre en el servicio,
        // sin confiar en dto.IsValid del cliente).
        const int maxBatch = 5000;
        if (request == null || request.Products == null || request.Products.Count == 0)
        {
            return BadRequest(new { message = "La solicitud de importación no contiene productos." });
        }
        if (request.Products.Count > maxBatch)
        {
            return BadRequest(new { message = $"El lote de importación excede el máximo permitido ({maxBatch} productos)." });
        }
        try
        {
            var result = await _inventoryService.BulkImportProductsAsync(request.Products, request.OverwriteMerge, cancellationToken);
            return Ok(new { added = result.added, updated = result.updated });
        }
        catch (System.UnauthorizedAccessException unEx)
        {
            return this.ApiForbidden(unEx.Message);
        }
    }

    [HttpGet("export")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> ExportProducts([FromQuery] string format = "xlsx", [FromQuery] bool activeOnly = true, [FromQuery] string? filter = null, System.Threading.CancellationToken cancellationToken = default)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "El rol Cajero no tiene permisos para exportar el catálogo de productos.");
        }
        try
        {
            var bytes = await _inventoryService.ExportProductsAsync(format, activeOnly, filter, cancellationToken);
            bool isXlsx = string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase) || string.Equals(format, "excel", StringComparison.OrdinalIgnoreCase);
            var contentType = isXlsx ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "text/csv; charset=utf-8";
            var ext = isXlsx ? "xlsx" : "csv";
            var filename = $"Productos_Catalogo_{DateTime.Now:yyyyMMdd_HHmmss}.{ext}";
            return File(bytes, contentType, filename);
        }
        catch (System.UnauthorizedAccessException unEx)
        {
            return this.ApiForbidden(unEx.Message);
        }
    }

    [HttpGet("export-template")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> ExportTemplate([FromQuery] string format = "xlsx", System.Threading.CancellationToken cancellationToken = default)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "El rol Cajero no tiene permisos para descargar la plantilla de importación.");
        }
        try
        {
            var bytes = await _inventoryService.GenerateTemplateAsync(format, cancellationToken);
            bool isXlsx = string.Equals(format, "xlsx", StringComparison.OrdinalIgnoreCase) || string.Equals(format, "excel", StringComparison.OrdinalIgnoreCase);
            var contentType = isXlsx ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" : "text/csv; charset=utf-8";
            var ext = isXlsx ? "xlsx" : "csv";
            var filename = $"Productos_Plantilla_Importacion.{ext}";
            return File(bytes, contentType, filename);
        }
        catch (System.UnauthorizedAccessException unEx)
        {
            return this.ApiForbidden(unEx.Message);
        }
    }
}
