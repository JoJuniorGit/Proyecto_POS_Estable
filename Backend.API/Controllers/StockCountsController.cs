using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Attributes;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

/// <summary>
/// 8.180-T1 (T1/S2-S4): API de conteo físico ciego (Paso 6). Lectura y conteo para
/// Admin/Manager/Cashier; creación, review, cierre, aplicación y cancelación solo Admin/Manager
/// (guard <c>CanMutateCatalog</c> + sello de seguridad). El detalle/scan son CIEGOS: el descuadre
/// solo se sirve por <c>GET {id}/review</c>.
/// </summary>
[ApiController]
[Authorize(Roles = "Admin,Manager,Cashier")]
[RequireSecurityStampValidation]
[Route("api/stock-counts")]
public sealed class StockCountsController : ControllerBase
{
    private readonly IStockCountService _stockCountService;
    private readonly ICurrentUserService _currentUserService;

    public StockCountsController(IStockCountService stockCountService, ICurrentUserService currentUserService)
    {
        ArgumentNullException.ThrowIfNull(stockCountService);
        ArgumentNullException.ThrowIfNull(currentUserService);
        _stockCountService = stockCountService;
        _currentUserService = currentUserService;
    }

    [HttpPost]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(StockCountSessionDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StockCountSessionDto>> CreateAsync(
        [FromBody] CreateStockCountRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return this.ApiForbidden("El rol Cajero no tiene permisos para crear sesiones de conteo físico.");
        }

        try
        {
            var created = await _stockCountService.CreateAsync(request, cancellationToken);
            return CreatedAtAction(nameof(GetByIdAsync), new { id = created.Id }, created);
        }
        catch (KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return this.ApiConflict(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<StockCountSessionDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<StockCountSessionDto>>> GetAllAsync(
        [FromQuery] StockCountStatus? status = null,
        CancellationToken cancellationToken = default)
    {
        var sessions = await _stockCountService.ListAsync(status, cancellationToken);
        return Ok(sessions);
    }

    [HttpGet("{id:int}")]
    [ProducesResponseType(typeof(StockCountDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StockCountDetailDto>> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await _stockCountService.GetDetailAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }

    [HttpPost("{id:int}/scan")]
    [ProducesResponseType(typeof(StockCountItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StockCountItemDto>> ScanAsync(
        int id,
        [FromBody] ScanStockCountRequestDto request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await _stockCountService.ScanAsync(id, request, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return this.ApiConflict(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
    }

    [HttpPut("{id:int}/items/{productId:int}")]
    [ProducesResponseType(typeof(StockCountItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StockCountItemDto>> SetCountedQuantityAsync(
        int id,
        int productId,
        [FromBody] SetCountedQuantityRequestDto request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await _stockCountService.SetCountedQuantityAsync(id, productId, request, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return this.ApiConflict(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
    }

    [HttpDelete("{id:int}/items/{productId:int}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveItemAsync(int id, int productId, CancellationToken cancellationToken = default)
    {
        try
        {
            await _stockCountService.RemoveItemAsync(id, productId, cancellationToken);
            return Ok(new { message = "Ítem quitado de la sesión de conteo." });
        }
        catch (KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return this.ApiConflict(ex.Message);
        }
    }

    [HttpGet("{id:int}/review")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(StockCountReviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<StockCountReviewDto>> ReviewAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            return Ok(await _stockCountService.ReviewAsync(id, cancellationToken));
        }
        catch (KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }

    [HttpPost("{id:int}/close")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(StockCountSessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StockCountSessionDto>> CloseAsync(int id, CancellationToken cancellationToken = default)
    {
        return await ExecuteSessionMutationAsync(id, mutate: service => service.CloseAsync(id, cancellationToken),
            forbiddenMessage: "El rol Cajero no tiene permisos para cerrar sesiones de conteo físico.");
    }

    [HttpPost("{id:int}/apply")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(StockCountSessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StockCountSessionDto>> ApplyAsync(int id, CancellationToken cancellationToken = default)
    {
        return await ExecuteSessionMutationAsync(id, mutate: service => service.ApplyAsync(id, cancellationToken),
            forbiddenMessage: "El rol Cajero no tiene permisos para aplicar sesiones de conteo físico.");
    }

    [HttpPost("{id:int}/cancel")]
    [Authorize(Roles = "Admin,Manager")]
    [ProducesResponseType(typeof(StockCountSessionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<ActionResult<StockCountSessionDto>> CancelAsync(int id, CancellationToken cancellationToken = default)
    {
        return await ExecuteSessionMutationAsync(id, mutate: service => service.CancelAsync(id, cancellationToken),
            forbiddenMessage: "El rol Cajero no tiene permisos para cancelar sesiones de conteo físico.");
    }

    /// <summary>
    /// Molde común de las mutaciones de sesión (close/apply/cancel): guard de rol, traducción de
    /// KeyNotFound → 404 y InvalidOperation → 409 vía ApiProblemResults.
    /// </summary>
    private async Task<ActionResult<StockCountSessionDto>> ExecuteSessionMutationAsync(
        int id,
        Func<IStockCountService, Task<StockCountSessionDto>> mutate,
        string forbiddenMessage)
    {
        if (!_currentUserService.CanMutateCatalog)
        {
            return this.ApiForbidden(forbiddenMessage);
        }

        try
        {
            return Ok(await mutate(_stockCountService));
        }
        catch (KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return this.ApiConflict(ex.Message);
        }
    }
}
