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
    public async Task<ActionResult<PagedResultDto<ProductDto>>> GetAll(
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
        var result = await _inventoryService.GetProductsPagedAsync(filter, page, pageSize, statusFilter: status, sortBy: sortBy, isDescending: isDescending, token: token);
        if (!_currentUserService.CanMutateCatalog && result.Items != null)
        {
            foreach (var item in result.Items)
            {
                item.CostPriceUSD = 0m;
                item.Cost = 0m;
                item.ProfitMarginRetail = 0m;
                item.ProfitMarginWholesale = 0m;
                item.ProfitPercentage = 0m;
            }
        }
        return result;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ProductDto>> GetById(int id)
    {
        var product = await _inventoryService.GetProductByIdAsync(id);
        if (product == null) return NotFound();
        var dto = MapToDto(product);
        if (!_currentUserService.CanMutateCatalog)
        {
            dto.CostPriceUSD = 0m;
            dto.Cost = 0m;
            dto.ProfitMarginRetail = 0m;
            dto.ProfitMarginWholesale = 0m;
            dto.ProfitPercentage = 0m;
        }
        return dto;
    }

    /// <summary>
    /// Crea un nuevo producto, grupo o variante mediante DTO protegido ([8B-CR1]).
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<ProductDto>> Create([FromBody] CreateProductDto request)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "El rol Cajero no tiene permisos para modificar el catálogo ni realizar importaciones.");
        }
        try
        {
            var product = new Product
            {
                Name = request.Name,
                SKU = request.SKU ?? string.Empty,
                Description = request.Description ?? string.Empty,
                PriceUSD = request.PriceUSD > 0 ? request.PriceUSD : request.PriceRetailUSD,
                PriceRetailUSD = request.PriceRetailUSD > 0 ? request.PriceRetailUSD : request.PriceUSD,
                PriceWholesaleUSD = request.PriceWholesaleUSD,
                CostPriceUSD = request.CostPriceUSD,
                ProfitMarginRetail = request.ProfitMarginRetail,
                ProfitMarginWholesale = request.ProfitMarginWholesale,
                MinWholesaleQuantity = request.MinWholesaleQuantity,
                HasWholesale = request.HasWholesale,
                IsFractional = request.IsFractional,
                UnitOfMeasure = request.UnitOfMeasure,
                LowStockThreshold = request.LowStockThreshold,
                IsCashAdvance = request.IsCashAdvance,
                IsActive = request.IsActive,
                ParentProductId = request.ParentProductId,
                IsGroupHeader = request.IsGroupHeader,
                IsStockShared = request.IsStockShared,
                HasIndependentPricing = request.HasIndependentPricing,
                ConversionFactor = request.ConversionFactor,
                GroupKey = request.GroupKey
            };

            var created = await _inventoryService.CreateProductAsync(product);
            return CreatedAtAction(nameof(GetById), new { id = created.Id }, MapToDto(created));
        }
        catch (System.UnauthorizedAccessException)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "No tiene permisos para realizar esta operación.");
        }
        catch (System.InvalidOperationException)
        {
            // 8B-M8: no filtrar ex.Message al cliente; el detalle queda en el log del servidor.
            return BadRequest("La operación no pudo completarse: los datos enviados no son válidos.");
        }
    }

    /// <summary>
    /// Actualiza un producto existente mediante DTO protegido ([8B-CR1]).
    /// </summary>
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateProductDto request)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "El rol Cajero no tiene permisos para modificar el catálogo ni realizar importaciones.");
        }
        if (id != request.Id) return BadRequest("El ID del producto no coincide.");
        try
        {
            var existing = await _inventoryService.GetProductByIdAsync(id);
            if (existing == null) return NotFound();

            existing.Name = request.Name;
            if (!string.IsNullOrWhiteSpace(request.SKU))
            {
                existing.SKU = request.SKU;
            }
            existing.Description = request.Description ?? string.Empty;
            existing.PriceRetailUSD = request.PriceRetailUSD > 0 ? request.PriceRetailUSD : request.PriceUSD;
            existing.PriceUSD = existing.PriceRetailUSD;
            existing.PriceWholesaleUSD = request.PriceWholesaleUSD;
            existing.CostPriceUSD = request.CostPriceUSD;
            existing.ProfitMarginRetail = request.ProfitMarginRetail;
            existing.ProfitMarginWholesale = request.ProfitMarginWholesale;
            existing.MinWholesaleQuantity = request.MinWholesaleQuantity;
            existing.HasWholesale = request.HasWholesale;
            existing.IsFractional = request.IsFractional;
            existing.UnitOfMeasure = request.UnitOfMeasure;
            existing.LowStockThreshold = request.LowStockThreshold;
            existing.IsCashAdvance = request.IsCashAdvance;
            existing.IsActive = request.IsActive;
            existing.ParentProductId = request.ParentProductId;
            existing.IsGroupHeader = request.IsGroupHeader;
            existing.IsStockShared = request.IsStockShared;
            existing.HasIndependentPricing = request.HasIndependentPricing;
            existing.ConversionFactor = request.ConversionFactor;
            existing.GroupKey = request.GroupKey;

            await _inventoryService.UpdateProductAsync(existing);
            return NoContent();
        }
        catch (System.UnauthorizedAccessException)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "No tiene permisos para realizar esta operación.");
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return NotFound();
        }
        catch (System.InvalidOperationException)
        {
            // 8B-M8: no filtrar ex.Message al cliente; el detalle queda en el log del servidor.
            return BadRequest("La operación no pudo completarse: los datos enviados no son válidos.");
        }
    }

    private ProductDto MapToDto(Product product)
    {
        bool canViewCost = _currentUserService.CanMutateCatalog;
        return new ProductDto
        {
            Id = product.Id,
            Name = product.Name,
            SKU = product.SKU,
            Description = product.Description,
            PriceUSD = product.PriceUSD,
            PriceRetailUSD = product.PriceRetailUSD,
            PriceWholesaleUSD = product.PriceWholesaleUSD,
            CostPriceUSD = canViewCost ? product.CostPriceUSD : 0m,
            ProfitMarginRetail = canViewCost ? product.ProfitMarginRetail : 0m,
            ProfitMarginWholesale = canViewCost ? product.ProfitMarginWholesale : 0m,
            MinWholesaleQuantity = product.MinWholesaleQuantity,
            HasWholesale = product.HasWholesale,
            IsFractional = product.IsFractional,
            PriceBsS = product.PriceBsS,
            Cost = canViewCost ? product.Cost : 0m,
            StockQuantity = product.StockQuantity,
            ProfitPercentage = canViewCost ? product.ProfitPercentage : 0m,
            UnitOfMeasure = product.UnitOfMeasure,
            LowStockThreshold = product.LowStockThreshold,
            IsCashAdvance = product.IsCashAdvance,
            IsActive = product.IsActive,
            IsDeleted = product.IsDeleted,
            ReservedQuantity = product.ReservedQuantity,
            ParentProductId = product.ParentProductId,
            ParentIsStockShared = product.ParentProduct != null && product.ParentProduct.IsStockShared,
            IsGroupHeader = product.IsGroupHeader,
            IsStockShared = product.IsStockShared,
            HasIndependentPricing = product.HasIndependentPricing,
            ConversionFactor = product.ConversionFactor,
            GroupKey = product.GroupKey
        };
    }

    // Aplica la máscara de costos (canViewCost) sobre DTOs devueltos por el servicio, de forma
    // consistente con MapToDto (hallazgo 8.4-N2: GetVariants/GetParents exponían CostPriceUSD
    // a cualquier rol autenticado).
    private List<Core.DTOs.ProductDto> MaskCostsForCurrentRole(List<Core.DTOs.ProductDto> items)
    {
        bool canViewCost = _currentUserService.CanMutateCatalog;
        if (canViewCost) return items;
        foreach (var item in items)
        {
            item.CostPriceUSD = 0m;
            item.ProfitMarginRetail = 0m;
            item.ProfitMarginWholesale = 0m;
            item.Cost = 0m;
            item.ProfitPercentage = 0m;
        }
        return items;
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
        catch (System.UnauthorizedAccessException)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "No tiene permisos para realizar esta operación.");
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
        catch (System.UnauthorizedAccessException)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "No tiene permisos para realizar esta operación.");
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
        catch (System.UnauthorizedAccessException)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "No tiene permisos para realizar esta operación.");
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
        catch (System.UnauthorizedAccessException)
        {
            return StatusCode(Microsoft.AspNetCore.Http.StatusCodes.Status403Forbidden, "No tiene permisos para realizar esta operación.");
        }
        catch (InvalidOperationException)
        {
            return this.ApiBadRequest("La operación no pudo completarse: los datos enviados no son válidos.");
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
        return Ok(MaskCostsForCurrentRole(variants));
    }

    [HttpGet("parents")]
    public async Task<ActionResult<List<Core.DTOs.ProductDto>>> GetParents()
    {
        var parents = await _inventoryService.GetParentProductsAsync();
        return Ok(MaskCostsForCurrentRole(parents));
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
        catch (DbUpdateConcurrencyException)
        {
            return this.ApiConflict("Conflicto de concurrencia al vincular variantes. Los datos fueron modificados por otro usuario.");
        }
        catch (KeyNotFoundException knfEx)
        {
            return this.ApiNotFound(knfEx.Message);
        }
        catch (InvalidOperationException invEx)
        {
            return this.ApiBadRequest(invEx.Message);
        }
        catch (UnauthorizedAccessException unEx)
        {
            return this.ApiForbidden(unEx.Message);
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
        catch (DbUpdateConcurrencyException)
        {
            return this.ApiConflict("Conflicto de concurrencia al desvincular la variante. Los datos fueron modificados por otro usuario.");
        }
        catch (KeyNotFoundException knfEx)
        {
            return this.ApiNotFound(knfEx.Message);
        }
        catch (InvalidOperationException invEx)
        {
            return this.ApiBadRequest(invEx.Message);
        }
        catch (UnauthorizedAccessException unEx)
        {
            return this.ApiForbidden(unEx.Message);
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
