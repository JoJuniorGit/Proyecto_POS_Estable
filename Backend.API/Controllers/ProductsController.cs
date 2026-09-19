using Core.Entities;
using Core.DTOs;
using Core.Extensions;
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
    private readonly IProductManagementService? _productManagementService;
    private readonly ICurrentUserService _currentUserService;

    public ProductsController(IInventoryService inventoryService, ICurrentUserService currentUserService)
        : this(inventoryService, inventoryService as IProductManagementService, currentUserService)
    {
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public ProductsController(IInventoryService inventoryService, IProductManagementService? productManagementService, ICurrentUserService currentUserService)
    {
        _inventoryService = inventoryService;
        _productManagementService = productManagementService ?? (inventoryService as IProductManagementService);
        _currentUserService = currentUserService;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResultDto<ProductDto>>> GetAllAsync(
        [FromQuery] string? filter = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? statusFilter = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] bool isDescending = false,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var result = await _inventoryService.GetProductsPagedAsync(filter, page, pageSize, statusFilter, sortBy, isDescending, cancellationToken);
        if (!_currentUserService.CanMutateCatalog)
        {
            result.Items = result.Items.Select(MaskProductDto).ToList();
        }
        return result;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ProductDto>> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var dto = _productManagementService != null
            ? await _productManagementService.GetProductDtoByIdAsync(id, cancellationToken)
            : null;
        if (dto == null)
        {
            var product = await _inventoryService.GetProductByIdAsync(id, cancellationToken);
            if (product != null)
            {
                dto = product.ToDto(_currentUserService.CanMutateCatalog);
            }
        }
        if (dto == null) return this.ApiNotFound("El producto solicitado no existe.");
        if (!_currentUserService.CanMutateCatalog)
        {
            return MaskProductDto(dto);
        }
        return dto;
    }

    [NonAction]
    public Task<ActionResult<ProductDto>> GetById(int id) => GetByIdAsync(id);

    [HttpPost]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<ProductDto>> CreateAsync([FromBody] CreateProductDto request, CancellationToken cancellationToken = default)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return this.ApiForbidden("El rol Cajero no tiene permisos para modificar el catálogo ni realizar importaciones.");
        }
        try
        {
            ProductDto? created = null;
            if (_productManagementService != null)
            {
                created = await _productManagementService.CreateProductFromDtoAsync(request, cancellationToken);
            }
            if (created == null)
            {
                decimal retailUsd = request.PriceRetailUSD > 0 ? request.PriceRetailUSD : request.PriceUSD;
                decimal todayRate = await _inventoryService.GetTodayExchangeRateAsync(cancellationToken);
                decimal canonicalPriceBsS = todayRate > 0
                    ? Core.Helpers.PricingCalculator.ToBsSCeiling(retailUsd, todayRate)
                    : Core.Helpers.PricingCalculator.RoundPriceUp(request.PriceBsS);
                var product = request.ToEntity(canonicalPriceBsS);
                var entityCreated = await _inventoryService.CreateProductAsync(product, cancellationToken);
                if (entityCreated != null)
                {
                    created = entityCreated.ToDto(_currentUserService.CanMutateCatalog);
                }
            }
            if (created != null && !_currentUserService.CanMutateCatalog)
            {
                created.MaskCosts();
            }
            return CreatedAtAction(nameof(GetByIdAsync), new { id = created?.Id }, created);
        }
        catch (System.UnauthorizedAccessException)
        {
            return this.ApiForbidden("No tiene permisos para realizar esta operación.");
        }
    }

    [NonAction]
    public Task<ActionResult<ProductDto>> Create(CreateProductDto request) => CreateAsync(request);

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> UpdateAsync(int id, [FromBody] UpdateProductDto request, CancellationToken cancellationToken = default)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return this.ApiForbidden("El rol Cajero no tiene permisos para modificar el catálogo ni realizar importaciones.");
        }
        if (id != request.Id) return this.ApiBadRequest("El ID del producto no coincide.");
        try
        {
            if (_productManagementService != null)
            {
                await _productManagementService.UpdateProductFromDtoAsync(id, request, cancellationToken);
            }
            else
            {
                return this.ApiProblem("Servicio de administración de productos no disponible.", StatusCodes.Status500InternalServerError, "Error Interno");
            }
            return NoContent();
        }
        catch (System.UnauthorizedAccessException)
        {
            return this.ApiForbidden("No tiene permisos para realizar esta operación.");
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return this.ApiNotFound($"Producto con ID {id} no encontrado.");
        }
    }

    [NonAction]
    public Task<IActionResult> Update(int id, [FromBody] UpdateProductDto request) => UpdateAsync(id, request);

    private List<Core.DTOs.ProductDto> MaskCostsForCurrentRole(List<Core.DTOs.ProductDto> items)
    {
        if (_currentUserService.CanMutateCatalog) return items;
        return items.MaskCosts();
    }

    [HttpPut("{id}/status")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> SetStatusAsync(int id, [FromBody] StatusUpdateDto dto, CancellationToken cancellationToken = default)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return this.ApiForbidden("El rol Cajero no tiene permisos para modificar el catálogo.");
        }
        try
        {
            await _inventoryService.SetProductStatusAsync(id, dto.IsActive, dto.IsDeleted, cancellationToken);
            return Ok(new { message = "Status updated successfully" });
        }
        catch (System.UnauthorizedAccessException)
        {
            return this.ApiForbidden("No tiene permisos para realizar esta operación.");
        }
    }

    [NonAction]
    public Task<IActionResult> SetStatus(int id, [FromBody] StatusUpdateDto dto) => SetStatusAsync(id, dto);

    [HttpPost("{id}/restore")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> RestoreAsync(int id, CancellationToken cancellationToken = default)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return this.ApiForbidden("El rol Cajero no tiene permisos para modificar el catálogo.");
        }
        try
        {
            await _inventoryService.RestoreProductAsync(id, cancellationToken);
            return Ok(new { message = "Product restored successfully" });
        }
        catch (System.UnauthorizedAccessException)
        {
            return this.ApiForbidden("No tiene permisos para realizar esta operación.");
        }
    }

    [NonAction]
    public Task<IActionResult> Restore(int id) => RestoreAsync(id);

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> DeleteAsync(int id, [FromQuery] bool hardDelete = false, CancellationToken cancellationToken = default)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return this.ApiForbidden("El rol Cajero no tiene permisos para eliminar registros del catálogo.");
        }
        try
        {
            var result = await _inventoryService.DeleteProductAsync(id, forceHardDelete: hardDelete, cancellationToken: cancellationToken);
            return Ok(new { result });
        }
        catch (System.UnauthorizedAccessException)
        {
            return this.ApiForbidden("No tiene permisos para realizar esta operación.");
        }
    }

    [NonAction]
    public Task<IActionResult> Delete(int id, [FromQuery] bool hardDelete = false) => DeleteAsync(id, hardDelete);

    [HttpPost("{id}/adjust-stock")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AdjustStockAsync(int id, [FromBody] DTOs.AdjustStockDto dto, CancellationToken cancellationToken = default)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return this.ApiForbidden(Core.Constants.InventoryMessages.UnauthorizedAdjustment);
        }

        try
        {
            await _inventoryService.AdjustStockAsync(id, dto.QuantityChange, dto.Reason, cancellationToken: cancellationToken);
            return NoContent();
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return this.ApiNotFound($"Producto con ID {id} no encontrado.");
        }
        catch (System.UnauthorizedAccessException)
        {
            return this.ApiForbidden("No tiene permisos para realizar esta operación.");
        }
    }

    [NonAction]
    public Task<IActionResult> AdjustStock(int id, [FromBody] DTOs.AdjustStockDto dto) => AdjustStockAsync(id, dto);

    private ProductDto MaskProductDto(ProductDto item) => item.MaskCosts();

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
    public async Task<ActionResult<Core.DTOs.ProductQuickInfoDto>> GetQuickInfoAsync(string sku, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sku) || !System.Text.RegularExpressions.Regex.IsMatch(sku.Trim(), @"^[A-Za-z0-9\-_]{1,50}$"))
        {
            return this.ApiBadRequest("El SKU debe contener entre 1 y 50 caracteres alfanuméricos (letras, dígitos, guiones o guiones bajos).");
        }
        var info = await _inventoryService.GetProductQuickInfoAsync(sku, cancellationToken: cancellationToken);
        if (info == null) return this.ApiNotFound($"Producto con SKU '{sku}' no encontrado.");
        
        if (!_currentUserService.CanMutateCatalog)
        {
            return MaskQuickInfoDto(info);
        }
        
        return info;
    }

    [NonAction]
    public Task<ActionResult<Core.DTOs.ProductQuickInfoDto>> GetQuickInfo(string sku) => GetQuickInfoAsync(sku);

    [HttpGet("suggestions")]
    public async Task<ActionResult<List<Core.DTOs.ProductQuickInfoDto>>> GetSuggestionsAsync([FromQuery] string filter, [FromQuery] bool activeOnly = true, System.Threading.CancellationToken token = default)
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

    [NonAction]
    public Task<ActionResult<List<Core.DTOs.ProductQuickInfoDto>>> GetSuggestions([FromQuery] string filter, [FromQuery] bool activeOnly = true, System.Threading.CancellationToken token = default) => GetSuggestionsAsync(filter, activeOnly, token);
}
