using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Core.DTOs;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

public partial class SalesController
{
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpGet("history")]
    public async Task<ActionResult> GetHistoryAsync(
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
        var (items, totalCount) = await _salesService.GetSalesHistoryAsync(page, pageSize, startDate, endDate, search, scopeToCashier ? cashierId : null);
        return Ok(new { Items = items, TotalCount = totalCount });
    }

    [NonAction]
    public Task<ActionResult> GetHistory(int page = 1, int pageSize = 20, System.DateTime? startDate = null, System.DateTime? endDate = null, string? search = null)
        => GetHistoryAsync(page, pageSize, startDate, endDate, search);

    [HttpGet("{id}/history-detail")]
    public async Task<ActionResult> GetHistoryDetailAsync(int id, CancellationToken cancellationToken = default)
    {
        if (!await IsAuthorizedForSaleAsync(id))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para consultar esta venta.");
        }

        try
        {
            var detail = await _salesService.GetSaleHistoryDetailAsync(id);
            return Ok(detail);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return this.ApiNotFound($"Detalle de historial para venta #{id} no encontrado.");
        }
    }

    [NonAction]
    public Task<ActionResult> GetHistoryDetail(int id) => GetHistoryDetailAsync(id);

    [HttpPut("{id}/price-list")]
    public async Task<ActionResult<SaleDto>> UpdatePriceListAsync(int id, [FromBody] UpdatePriceListRequestDto request, CancellationToken cancellationToken = default)
    {
        if (!await IsAuthorizedForSaleAsync(id))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para modificar esta venta.");
        }

        try
        {
            var sale = await _salesService.UpdatePriceListAsync(id, request.PriceListType, GetActorUserId());
            return Ok(sale);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }

    [NonAction]
    public Task<ActionResult<SaleDto>> UpdatePriceList(int id, [FromBody] UpdatePriceListRequestDto request) => UpdatePriceListAsync(id, request);
}