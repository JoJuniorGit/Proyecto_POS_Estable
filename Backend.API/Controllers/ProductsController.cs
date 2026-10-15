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

[Authorize(Roles = "Admin,Manager,Cashier")]
[ApiController]
[Route("api/[controller]")]
public partial class ProductsController : ControllerBase
{
    private readonly IInventoryService _inventoryService;
    private readonly IProductManagementService _productManagementService;
    private readonly ICurrentUserService _currentUserService;
    private readonly Backend.API.Services.IdempotencyRequestResolver _idempotencyResolver;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public ProductsController(
        IInventoryService inventoryService,
        IProductManagementService productManagementService,
        ICurrentUserService currentUserService,
        IIdempotencyService? idempotencyService = null)
    {
        _inventoryService = inventoryService;
        _productManagementService = productManagementService;
        _currentUserService = currentUserService;
        _idempotencyResolver = new Backend.API.Services.IdempotencyRequestResolver(idempotencyService, currentUserService);
    }

    [HttpGet]
    public async Task<ActionResult<PagedResultDto<ProductDto>>> GetAllAsync(
        [FromQuery] string? filter = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        [FromQuery] string? statusFilter = null,
        [FromQuery] string? sortBy = null,
        [FromQuery] bool isDescending = false,
        [FromQuery] int? categoryId = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        if (!_currentUserService.CanMutateCatalog && string.Equals(sortBy, "cost", System.StringComparison.OrdinalIgnoreCase))
        {
            sortBy = "name";
        }

        // 8.179-T1 (S4): categoryId filtra por la categoría y todo su subárbol.
        var result = await _inventoryService.GetProductsPagedAsync(filter, page, pageSize, statusFilter, sortBy, isDescending, cancellationToken, categoryId);
        if (!_currentUserService.CanMutateCatalog)
        {
            result.Items = result.Items.Select(MaskProductDto).ToList();
        }
        return result;
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<ProductDto>> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var dto = await _productManagementService.GetProductDtoByIdAsync(id, cancellationToken);
        if (dto == null) return this.ApiNotFound("El producto solicitado no existe.");
        if (!_currentUserService.CanMutateCatalog)
        {
            return MaskProductDto(dto);
        }
        return dto;
    }

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
            ProductDto? created = await _productManagementService.CreateProductFromDtoAsync(request, cancellationToken);
            return CreatedAtAction(nameof(GetByIdAsync), new { id = created?.Id }, created);
        }
        catch (System.UnauthorizedAccessException)
        {
            return this.ApiForbidden("No tiene permisos para realizar esta operación.");
        }
    }

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
            await _productManagementService.UpdateProductFromDtoAsync(id, request, cancellationToken);
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

        // 8.177-T1: validación explícita de la clasificación opcional (el ModelState automático no
        // cubre llamadas directas y el binding numérico admite valores de enum indefinidos).
        if (dto.ReasonCode.HasValue && !System.Enum.IsDefined(dto.ReasonCode.Value))
        {
            return this.ApiBadRequest("El motivo estandarizado del ajuste no es válido.");
        }

        if (dto.Observation is { Length: > 500 })
        {
            return this.ApiBadRequest("La observación no puede exceder los 500 caracteres.");
        }

        string requestPath = $"/api/products/{id}/adjust-stock";
        var idempotency = await _idempotencyResolver.ResolveIfPresentAsync(
            this,
            requestPath,
            System.Text.Json.JsonSerializer.Serialize(dto));
        if (idempotency.ShouldStop)
        {
            return idempotency.BlockingResult!;
        }

        try
        {
            await _inventoryService.AdjustStockAsync(
                id,
                dto.QuantityChange,
                dto.Reason,
                reasonCode: dto.ReasonCode,
                observation: dto.Observation,
                cancellationToken: cancellationToken);
            await _idempotencyResolver.RegisterSuccessAsync(idempotency, requestPath, "", cancellationToken);
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
        catch (DbUpdateException ex) when (Backend.API.Services.IdempotencyRequestResolver.IsIdempotencyUniqueViolation(ex))
        {
            return await _idempotencyResolver.HandleCollisionAsync(this, ex, requestPath, idempotency.Key, idempotency.PayloadHash);
        }
    }

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

    [HttpGet("suggestions")]
    public async Task<ActionResult<List<Core.DTOs.ProductQuickInfoDto>>> GetSuggestionsAsync([FromQuery] string filter, [FromQuery] bool activeOnly = true, [FromQuery] int? categoryId = null, System.Threading.CancellationToken token = default)
    {
        // 8.179-T1 (S4): búsqueda POS acotada al subárbol de la categoría cuando se provee.
        var results = await _inventoryService.GetSuggestionsAsync(filter, activeOnly, token, categoryId);
        
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
