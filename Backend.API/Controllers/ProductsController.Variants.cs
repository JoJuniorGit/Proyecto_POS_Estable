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

    [HttpGet("{id}/variants")]
    public async Task<ActionResult<List<Core.DTOs.ProductDto>>> GetVariantsAsync(int id, CancellationToken token = default)
    {
        var variants = await _inventoryService.GetVariantOptionsAsync(id, token);
        return Ok(MaskCostsForCurrentRole(variants));
    }

    [NonAction]
    public Task<ActionResult<List<Core.DTOs.ProductDto>>> GetVariants(int id) => GetVariantsAsync(id);

    [HttpGet("parents")]
    public async Task<ActionResult<List<Core.DTOs.ProductDto>>> GetParentsAsync(CancellationToken token = default)
    {
        var parents = await _inventoryService.GetParentProductsAsync(token);
        return Ok(MaskCostsForCurrentRole(parents));
    }

    [NonAction]
    public Task<ActionResult<List<Core.DTOs.ProductDto>>> GetParents() => GetParentsAsync();

    [HttpGet("{parentId}/candidate-variants")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>>> GetCandidateVariantsAsync(
        int parentId,
        [FromQuery] string? filter = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken token = default)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return this.ApiForbidden("No tiene permisos para consultar candidatos de variantes.");
        }

        var result = await _inventoryService.GetCandidateVariantsPagedAsync(parentId, filter, page, pageSize, token);
        return Ok(result);
    }

    [NonAction]
    public Task<ActionResult<Core.DTOs.PagedResultDto<Core.DTOs.ProductDto>>> GetCandidateVariants(
        int parentId,
        [FromQuery] string? filter = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken token = default) => GetCandidateVariantsAsync(parentId, filter, page, pageSize, token);

    [HttpPost("{parentId}/link-variants")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(List<Core.DTOs.ProductDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<List<Core.DTOs.ProductDto>>> LinkVariantsBatchAsync(
        int parentId,
        [FromBody] List<int> productIds,
        CancellationToken token = default)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return this.ApiForbidden("No tiene permisos para vincular variantes al catálogo.");
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
        catch (UnauthorizedAccessException unEx)
        {
            return this.ApiForbidden(unEx.Message);
        }
    }

    [NonAction]
    public Task<ActionResult<List<Core.DTOs.ProductDto>>> LinkVariantsBatch(
        int parentId,
        [FromBody] List<int> productIds,
        CancellationToken token = default) => LinkVariantsBatchAsync(parentId, productIds, token);

    [HttpPost("{parentId}/unlink-variant/{variantId}")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(Core.DTOs.ProductDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<ActionResult<Core.DTOs.ProductDto>> UnlinkVariantAsync(
        int parentId,
        int variantId,
        CancellationToken token = default)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return this.ApiForbidden("No tiene permisos para desvincular variantes del catálogo.");
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
        catch (UnauthorizedAccessException unEx)
        {
            return this.ApiForbidden(unEx.Message);
        }
    }

    [NonAction]
    public Task<ActionResult<Core.DTOs.ProductDto>> UnlinkVariant(
        int parentId,
        int variantId,
        CancellationToken token = default) => UnlinkVariantAsync(parentId, variantId, token);
}
