using Core.Entities;
using Core.DTOs;
using Core.Interfaces;
using Backend.API.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public partial class ProductsController : ControllerBase
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
            var maskedItems = new List<ProductDto>();
            foreach (var item in result.Items)
            {
                maskedItems.Add(MaskProductDto(item));
            }
            result.Items = maskedItems;
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
            return MaskProductDto(dto);
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
            decimal retailUsd = request.PriceRetailUSD > 0 ? request.PriceRetailUSD : request.PriceUSD;
            decimal todayRate = await _inventoryService.GetTodayExchangeRateAsync();
            decimal canonicalPriceBsS = todayRate > 0
                ? Core.Helpers.PricingCalculator.ToBsSCeiling(retailUsd, todayRate)
                : Core.Helpers.PricingCalculator.RoundPriceUp(request.PriceBsS);

            var product = new Product
            {
                Name = request.Name,
                SKU = request.SKU ?? string.Empty,
                Description = request.Description ?? string.Empty,
                PriceUSD = request.PriceUSD > 0 ? request.PriceUSD : request.PriceRetailUSD,
                PriceRetailUSD = request.PriceRetailUSD > 0 ? request.PriceRetailUSD : request.PriceUSD,
                PriceBsS = canonicalPriceBsS,
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
            decimal todayRate = await _inventoryService.GetTodayExchangeRateAsync();
            if (existing.PriceRetailUSD > 0 && todayRate > 0)
            {
                existing.PriceBsS = Core.Helpers.PricingCalculator.ToBsSCeiling(existing.PriceRetailUSD, todayRate);
            }
            else if (request.PriceBsS > 0)
            {
                existing.PriceBsS = Core.Helpers.PricingCalculator.RoundPriceUp(request.PriceBsS);
            }

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
    }

    private ProductDto MaskProductDto(ProductDto item)
    {
        return new ProductDto
        {
            Id = item.Id,
            Name = item.Name,
            SKU = item.SKU,
            Description = item.Description,
            PriceUSD = item.PriceUSD,
            PriceRetailUSD = item.PriceRetailUSD,
            PriceWholesaleUSD = item.PriceWholesaleUSD,
            CostPriceUSD = 0m,
            ProfitMarginRetail = 0m,
            ProfitMarginWholesale = 0m,
            MinWholesaleQuantity = item.MinWholesaleQuantity,
            HasWholesale = item.HasWholesale,
            IsFractional = item.IsFractional,
            UnitOfMeasure = item.UnitOfMeasure,
            PriceBsS = item.PriceBsS,
            Cost = 0m,
            StockQuantity = item.StockQuantity,
            ProfitPercentage = 0m,
            LowStockThreshold = item.LowStockThreshold,
            IsCashAdvance = item.IsCashAdvance,
            IsActive = item.IsActive,
            IsDeleted = item.IsDeleted,
            ReservedQuantity = item.ReservedQuantity,
            ParentProductId = item.ParentProductId,
            ParentIsStockShared = item.ParentIsStockShared,
            IsGroupHeader = item.IsGroupHeader,
            IsStockShared = item.IsStockShared,
            HasIndependentPricing = item.HasIndependentPricing,
            ConversionFactor = item.ConversionFactor,
            GroupKey = item.GroupKey,
            VariantCount = item.VariantCount,
            ConsolidatedStock = item.ConsolidatedStock,
            Variants = item.Variants?.Select(v => MaskProductDto(v)).ToList()
        };
    }

    private Core.DTOs.ProductQuickInfoDto MaskQuickInfoDto(Core.DTOs.ProductQuickInfoDto item)
    {
        return new Core.DTOs.ProductQuickInfoDto
        {
            Id = item.Id,
            SKU = item.SKU,
            Name = item.Name,
            PriceUSD = item.PriceUSD,
            PriceRetailUSD = item.PriceRetailUSD,
            PriceWholesaleUSD = item.PriceWholesaleUSD,
            PriceBsS = item.PriceBsS,
            HasWholesale = item.HasWholesale,
            IsFractional = item.IsFractional,
            UnitOfMeasure = item.UnitOfMeasure,
            MinWholesaleQuantity = item.MinWholesaleQuantity,
            StockQuantity = item.StockQuantity,
            IsCashAdvance = item.IsCashAdvance,
            IsActive = item.IsActive,
            ProfitPercentage = 0m,
            ReservedQuantity = item.ReservedQuantity,
            ParentProductId = item.ParentProductId,
            ParentIsStockShared = item.ParentIsStockShared,
            IsGroupHeader = item.IsGroupHeader,
            IsStockShared = item.IsStockShared,
            HasIndependentPricing = item.HasIndependentPricing,
            ConversionFactor = item.ConversionFactor,
            VariantCount = item.VariantCount,
            ConsolidatedStock = item.ConsolidatedStock
        };
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
        
        if (!_currentUserService.CanMutateCatalog)
        {
            return MaskQuickInfoDto(info);
        }
        
        return info;
    }

    [HttpGet("suggestions")]
    public async Task<ActionResult<List<Core.DTOs.ProductQuickInfoDto>>> GetSuggestions([FromQuery] string filter, [FromQuery] bool activeOnly = true, System.Threading.CancellationToken token = default)
    {
        var results = await _inventoryService.GetSuggestionsAsync(filter, activeOnly, token);
        
        if (!_currentUserService.CanMutateCatalog && results != null)
        {
            var masked = new List<Core.DTOs.ProductQuickInfoDto>(results.Count);
            foreach (var r in results)
            {
                masked.Add(MaskQuickInfoDto(r));
            }
            return Ok(masked);
        }
        
        return Ok(results);
    }
}
