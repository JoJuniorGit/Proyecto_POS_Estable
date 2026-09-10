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
        catch (UnauthorizedAccessException unEx)
        {
            return this.ApiForbidden(unEx.Message);
        }
    }
}
