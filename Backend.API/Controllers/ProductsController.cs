using Core.Entities;
using Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

[Authorize]
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
        [FromQuery] string? sortBy,
        [FromQuery] bool isDescending = false,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        System.Threading.CancellationToken token = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);
        return await _inventoryService.GetProductsPagedAsync(filter, page, pageSize, statusFilter: status, sortBy: sortBy, isDescending: isDescending, token: token);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<Product>> GetById(int id)
    {
        var product = await _inventoryService.GetProductByIdAsync(id);
        if (product == null) return NotFound();
        return product;
    }

    /// <summary>
    /// Crea un nuevo producto, grupo o variante.
    /// Para variantes de grupos con stock compartido (IsStockShared = true), ConversionFactor define las unidades base a descontar.
    /// Para productos no compartidos o grupos, ConversionFactor se normaliza automáticamente a 1.0000.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Admin,Manager")]
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
        catch (System.InvalidOperationException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Actualiza un producto existente.
    /// Si ConversionFactor se omite o es menor o igual a 0 en una variante con stock compartido, se conserva el valor existente.
    /// </summary>
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin,Manager")]
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
    }

    [HttpPut("{id}/status")]
    [Authorize(Roles = "Admin,Manager")]
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
    [Authorize(Roles = "Admin,Manager")]
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
    [Authorize(Roles = "Admin,Manager")]
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

    /// <summary>
    /// Realiza un ajuste manual de stock para un producto según la modalidad de inventario.
    /// </summary>
    /// <remarks>
    /// Restricciones de Inventario:
    /// - Productos Eliminados/Archivados: Rechazado (400 Bad Request).
    /// - Grupos con Stock Individual: Rechazado (el stock es la suma consolidada de sus variantes).
    /// - Variantes con Stock Compartido: Rechazado (el stock pertenece al pool central del producto padre).
    /// - Servicios / Adelantos de Efectivo: Rechazado (no manejan inventario físico).
    /// </remarks>
    /// <response code="204">Ajuste procesado exitosamente.</response>
    /// <response code="400">Si la modalidad de inventario no permite ajustes directos o los datos son inválidos.</response>
    /// <response code="403">Si el usuario no tiene permisos de mutación de catálogo.</response>
    /// <response code="404">Si el producto no existe.</response>
    [HttpPost("{id}/adjust-stock")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AdjustStock(int id, [FromBody] DTOs.AdjustStockDto dto)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return StatusCode(StatusCodes.Status403Forbidden, Core.Constants.InventoryMessages.UnauthorizedAdjustment);
        }

        try
        {
            await _inventoryService.AdjustStockAsync(id, dto.QuantityChange, dto.Reason);
            return NoContent();
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return NotFound();
        }
        catch (System.UnauthorizedAccessException unEx)
        {
            return StatusCode(StatusCodes.Status403Forbidden, unEx.Message);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
    }

    [HttpGet("quick-check/{sku}")]
    public async Task<ActionResult<Core.DTOs.ProductQuickInfoDto>> GetQuickInfo(string sku)
    {
        if (string.IsNullOrWhiteSpace(sku) || !System.Text.RegularExpressions.Regex.IsMatch(sku.Trim(), @"^[A-Za-z0-9\-_]{1,50}$"))
        {
            return BadRequest("El SKU debe contener entre 1 y 50 caracteres alfanuméricos (letras, dígitos, guiones o guiones bajos).");
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

    [HttpGet("{id}/variants")]
    public async Task<ActionResult<List<Core.DTOs.ProductDto>>> GetVariants(int id)
    {
        var variants = await _inventoryService.GetVariantOptionsAsync(id);
        return Ok(variants);
    }

    [HttpGet("parents")]
    public async Task<ActionResult<List<Core.DTOs.ProductDto>>> GetParents()
    {
        var parents = await _inventoryService.GetParentProductsAsync();
        return Ok(parents);
    }

    /// <summary>
    /// Obtiene una lista paginada de productos candidatos para ser vinculados como variantes de un producto padre.
    /// Excluye el propio padre, variantes ya asignadas a este padre, otros agrupadores y productos de avance de efectivo.
    /// </summary>
    /// <param name="parentId">ID del producto padre.</param>
    /// <param name="filter">Término de búsqueda por nombre o SKU.</param>
    /// <param name="page">Número de página (1-indexed).</param>
    /// <param name="pageSize">Cantidad de registros por página.</param>
    /// <param name="token">Token de cancelación.</param>
    [HttpGet("{parentId}/candidate-variants")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>>> GetCandidateVariants(
        int parentId,
        [FromQuery] string? filter = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken token = default)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "No tiene permisos para consultar candidatos de variantes.");
        }

        var result = await _inventoryService.GetCandidateVariantsPagedAsync(parentId, filter, page, pageSize, token);
        return Ok(result);
    }

    /// <summary>
    /// Vincula de forma atómica y transaccional una lista de productos existentes como variantes del producto padre.
    /// </summary>
    /// <param name="parentId">ID del producto padre.</param>
    /// <param name="productIds">Lista de IDs de productos a vincular.</param>
    /// <param name="token">Token de cancelación.</param>
    [HttpPost("{parentId}/link-variants")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(List<Core.DTOs.ProductDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<List<Core.DTOs.ProductDto>>> LinkVariantsBatch(
        int parentId,
        [FromBody] List<int> productIds,
        CancellationToken token = default)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "No tiene permisos para vincular variantes al catálogo.");
        }

        try
        {
            var updatedVariants = await _inventoryService.LinkVariantsBatchAsync(parentId, productIds, token);
            return Ok(updatedVariants);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            return StatusCode(StatusCodes.Status409Conflict, new { message = "Conflicto de concurrencia al vincular variantes. Los datos fueron modificados por otro usuario.", details = ex.Message });
        }
        catch (KeyNotFoundException knfEx)
        {
            return NotFound(new { message = knfEx.Message });
        }
        catch (InvalidOperationException invEx)
        {
            return BadRequest(new { message = invEx.Message });
        }
        catch (UnauthorizedAccessException unEx)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = unEx.Message });
        }
    }

    /// <summary>
    /// Desvincula una variante de su producto padre, normalizando su stock y preservando su umbral de stock bajo.
    /// </summary>
    /// <param name="parentId">ID del producto padre.</param>
    /// <param name="variantId">ID de la variante a desvincular.</param>
    /// <param name="token">Token de cancelación.</param>
    [HttpPost("{parentId}/unlink-variant/{variantId}")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(Core.DTOs.ProductDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<Core.DTOs.ProductDto>> UnlinkVariant(
        int parentId,
        int variantId,
        CancellationToken token = default)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return StatusCode(StatusCodes.Status403Forbidden, "No tiene permisos para desvincular variantes del catálogo.");
        }

        try
        {
            var unlinked = await _inventoryService.UnlinkVariantAsync(parentId, variantId, token);
            return Ok(unlinked);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            return StatusCode(StatusCodes.Status409Conflict, new { message = "Conflicto de concurrencia al desvincular la variante. Los datos fueron modificados por otro usuario.", details = ex.Message });
        }
        catch (KeyNotFoundException knfEx)
        {
            return NotFound(new { message = knfEx.Message });
        }
        catch (InvalidOperationException invEx)
        {
            return BadRequest(new { message = invEx.Message });
        }
        catch (UnauthorizedAccessException unEx)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = unEx.Message });
        }
    }

    [HttpPost("bulk-import")]
    [Authorize(Roles = "Admin,Manager")]
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
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, unEx.Message);
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
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, unEx.Message);
        }
    }
}
