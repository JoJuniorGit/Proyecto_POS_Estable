using Core.Entities;
using Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ProductsController : ControllerBase
{
    private readonly IInventoryService _inventoryService;
    private readonly ICurrentUserService _currentUserService;

    public ProductsController(IInventoryService inventoryService, ICurrentUserService currentUserService)
    {
        _inventoryService = inventoryService;
        _currentUserService = currentUserService;
    }

    [HttpGet]
    public async Task<ActionResult<Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>>> GetAll(
        [FromQuery] string? filter,
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        System.Threading.CancellationToken token = default)
    {
        return await _inventoryService.GetProductsPagedAsync(filter, page, pageSize, statusFilter: status, token: token);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Product>> GetById(int id)
    {
        try
        {
            var product = await _inventoryService.GetProductByIdAsync(id);
            if (product == null) return NotFound();
            return product;
        }
        catch (System.Exception ex)
        {
            return StatusCode(500, $"Error al obtener producto: {ex.Message}");
        }
    }

    [HttpPost]
    public async Task<ActionResult<Product>> Create(Product product)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "El rol Cajero no tiene permisos para modificar el catálogo ni realizar importaciones.");
        }
        try
        {
            var created = await _inventoryService.CreateProductAsync(product);
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
        }
        catch (System.UnauthorizedAccessException unEx)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, unEx.Message);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateException dbEx)
        {
            var innerMessage = dbEx.InnerException?.Message ?? dbEx.Message;
            return StatusCode(500, $"Database Error (Pending Migration or Constraint): {innerMessage}");
        }
        catch (System.InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (System.Exception ex)
        {
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Update(int id, Product product)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "El rol Cajero no tiene permisos para modificar el catálogo ni realizar importaciones.");
        }
        if (id != product.Id) return BadRequest("El ID del producto no coincide.");
        try
        {
            await _inventoryService.UpdateProductAsync(product);
            return NoContent();
        }
        catch (System.UnauthorizedAccessException unEx)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, unEx.Message);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return NotFound();
        }
        catch (System.InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (System.Exception ex)
        {
            return StatusCode(500, $"Error interno al actualizar producto: {ex.Message}");
        }
    }

    [HttpPut("{id}/status")]
    public async Task<IActionResult> SetStatus(int id, [FromBody] StatusUpdateDto dto)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "El rol Cajero no tiene permisos para modificar el catálogo.");
        }
        try
        {
            await _inventoryService.SetProductStatusAsync(id, dto.IsActive, dto.IsDeleted);
            return Ok(new { message = "Status updated successfully" });
        }
        catch (System.UnauthorizedAccessException unEx)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, unEx.Message);
        }
    }

    [HttpPost("{id}/restore")]
    public async Task<IActionResult> Restore(int id)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "El rol Cajero no tiene permisos para modificar el catálogo.");
        }
        try
        {
            await _inventoryService.RestoreProductAsync(id);
            return Ok(new { message = "Product restored successfully" });
        }
        catch (System.UnauthorizedAccessException unEx)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, unEx.Message);
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id, [FromQuery] bool hardDelete = false)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "El rol Cajero no tiene permisos para eliminar registros del catálogo.");
        }
        try
        {
            var result = await _inventoryService.DeleteProductAsync(id, forceHardDelete: hardDelete);
            return Ok(new { result });
        }
        catch (System.UnauthorizedAccessException unEx)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, unEx.Message);
        }
    }

public class StatusUpdateDto
{
    public bool IsActive { get; set; }
    public bool IsDeleted { get; set; }
}

    [HttpPost("{id}/adjust-stock")]
    public async Task<IActionResult> AdjustStock(int id, [FromBody] DTOs.AdjustStockDto dto)
    {
        try
        {
            await _inventoryService.UpdateStockAsync(id, dto.QuantityChange, dto.Reason);
            return NoContent();
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpGet("quick-check/{sku}")]
    public async Task<ActionResult<Core.DTOs.ProductQuickInfoDto>> GetQuickInfo(string sku)
    {
        if (string.IsNullOrWhiteSpace(sku) || !System.Text.RegularExpressions.Regex.IsMatch(sku.Trim(), @"^\d+$"))
        {
            return BadRequest("El SKU debe ser estrictamente un número entero (solo dígitos 0-9).");
        }
        var info = await _inventoryService.GetProductQuickInfoAsync(sku);
        if (info == null) return NotFound();
        return info;
    }

    [HttpGet("suggestions")]
    public async Task<ActionResult<List<Core.DTOs.ProductQuickInfoDto>>> GetSuggestions([FromQuery] string filter, [FromQuery] bool activeOnly = true, System.Threading.CancellationToken token = default)
    {
        var results = await _inventoryService.GetSuggestionsAsync(filter, activeOnly, token);
        return Ok(results);
    }

    [HttpPost("bulk-import")]
    public async Task<IActionResult> BulkImport([FromBody] Core.DTOs.BulkImportRequestDto request, System.Threading.CancellationToken cancellationToken)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "El rol Cajero no tiene permisos para realizar importaciones.");
        }
        try
        {
            var result = await _inventoryService.BulkImportProductsAsync(request.Products, request.OverwriteMerge, cancellationToken);
            return Ok(new { added = result.added, updated = result.updated });
        }
        catch (System.UnauthorizedAccessException unEx)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, unEx.Message);
        }
        catch (System.Exception ex)
        {
            return StatusCode(500, $"Internal Server Error: {ex.Message}");
        }
    }

    [HttpGet("export")]
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
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, unEx.Message);
        }
        catch (System.Exception ex)
        {
            return StatusCode(500, $"Error al exportar productos: {ex.Message}");
        }
    }

    [HttpGet("export-template")]
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
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, unEx.Message);
        }
        catch (System.Exception ex)
        {
            return StatusCode(500, $"Error al generar plantilla: {ex.Message}");
        }
    }
}
