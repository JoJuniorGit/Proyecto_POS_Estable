using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Core.DTOs;
using Sales.Module.DTOs;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

public partial class SalesController
{
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpGet("history")]
    [ProducesResponseType(typeof(PagedResultDto<SaleHistoryDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<PagedResultDto<SaleHistoryDto>>> GetHistoryAsync(
        [FromQuery] int page = 1, 
        [FromQuery] int pageSize = 20, 
        [FromQuery] System.DateTime? startDate = null, 
        [FromQuery] System.DateTime? endDate = null, 
        [FromQuery] string? search = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var (scopeToCashier, cashierId) = GetCashierReadScope();
        var (items, totalCount) = await _salesService.GetSalesHistoryAsync(page, pageSize, startDate, endDate, search, scopeToCashier ? cashierId : null, cancellationToken);
        var hasMore = (page * pageSize) < totalCount;
        return Ok(new PagedResultDto<SaleHistoryDto>(items, totalCount, hasMore));
    }

    [HttpGet("{id}/history-detail")]
    [ProducesResponseType(typeof(SaleHistoryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<SaleHistoryDto>> GetHistoryDetailAsync(int id, CancellationToken cancellationToken = default)
    {
        if (!await IsAuthorizedForSaleAsync(id, cancellationToken))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para consultar esta venta.");
        }

        try
        {
            var detail = await _salesService.GetSaleHistoryDetailAsync(id, cancellationToken);
            return Ok(detail);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return this.ApiNotFound($"Detalle de historial para venta #{id} no encontrado.");
        }
    }

    [HttpPut("{id}/price-list")]
    public async Task<ActionResult<SaleDto>> UpdatePriceListAsync(int id, [FromBody] UpdatePriceListRequestDto request, CancellationToken cancellationToken = default)
    {
        if (!await IsAuthorizedForSaleAsync(id, cancellationToken))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para modificar esta venta.");
        }

        try
        {
            var sale = await _salesService.UpdatePriceListAsync(id, request.PriceListType, GetActorUserId(), cancellationToken);
            return Ok(sale);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }
}