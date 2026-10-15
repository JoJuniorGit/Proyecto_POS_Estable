using Core.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

public partial class ProductsController
{
    // 8.181-T1 (S1): edición masiva de precios. Ambos endpoints van restringidos a Admin/Manager
    // (misma política que el resto de mutaciones del catálogo) y comparten el contrato
    // BulkPriceUpdateRequestDto; el apply devuelve el mismo resultado que el preview porque
    // comparten motor de cálculo (S4).
    [HttpPost("bulk-prices/preview")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(BulkPriceUpdateResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<BulkPriceUpdateResultDto>> PreviewBulkPricesAsync([FromBody] BulkPriceUpdateRequestDto request, CancellationToken cancellationToken = default)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return this.ApiForbidden("El rol Cajero no tiene permisos para modificar el catálogo.");
        }

        try
        {
            var result = await _inventoryService.PreviewBulkPriceUpdateAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (System.UnauthorizedAccessException)
        {
            return this.ApiForbidden("No tiene permisos para realizar esta operación.");
        }
        catch (System.ArgumentException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }

    [HttpPost("bulk-prices/apply")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(BulkPriceUpdateResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<BulkPriceUpdateResultDto>> ApplyBulkPricesAsync([FromBody] BulkPriceUpdateRequestDto request, CancellationToken cancellationToken = default)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return this.ApiForbidden("El rol Cajero no tiene permisos para modificar el catálogo.");
        }

        try
        {
            var result = await _inventoryService.BulkUpdatePricesAsync(request, cancellationToken);
            return Ok(result);
        }
        catch (System.UnauthorizedAccessException)
        {
            return this.ApiForbidden("No tiene permisos para realizar esta operación.");
        }
        catch (DbUpdateConcurrencyException)
        {
            // 8.181-T1 (S1): conflicto de concurrencia (xmin) con rollback total en el servicio.
            return this.ApiConflict("No se aplicó la actualización masiva: los precios fueron modificados por otro usuario. Vuelva a cargar el catálogo e intente nuevamente.");
        }
        catch (System.ArgumentException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }
}
