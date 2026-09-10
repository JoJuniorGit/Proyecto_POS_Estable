using Microsoft.AspNetCore.Mvc;
using Core.DTOs;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

public partial class SalesController
{
    /// <summary>
    /// Obtiene el historial paginado de ventas completadas con filtros opcionales de fecha y término de búsqueda.
    /// </summary>
    /// <param name="page">Número de página (base 1, por defecto 1).</param>
    /// <param name="pageSize">Cantidad de registros por página (1 a 100, por defecto 20).</param>
    /// <param name="startDate">Fecha inicial del filtro (formato 'yyyy-MM-dd' o ISO 8601). Se interpreta según la hora legal de Venezuela (UTC-4, VET) desde las 00:00:00 locales.</param>
    /// <param name="endDate">Fecha final del filtro (formato 'yyyy-MM-dd' o ISO 8601). Se interpreta según la hora legal de Venezuela (UTC-4, VET) cubriendo hasta las 23:59:59.999 locales. Si se omite habiendo startDate, asume el día actual.</param>
    /// <param name="search">Término de búsqueda multicampo (N° factura, cédula/nombre cliente o cajero).</param>
    [HttpGet("history")]
    public async Task<ActionResult> GetHistory([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] System.DateTime? startDate = null, [FromQuery] System.DateTime? endDate = null, [FromQuery] string? search = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var (scopeToCashier, cashierId) = GetCashierReadScope();
        var (items, totalCount) = await _salesService.GetSalesHistoryAsync(page, pageSize, startDate, endDate, search, scopeToCashier ? cashierId : null);
        return Ok(new { Items = items, TotalCount = totalCount });
    }

    [HttpGet("{id}/history-detail")]
    public async Task<ActionResult> GetHistoryDetail(int id)
    {
        // 8.9-B2: ownership a nivel de objeto, igual que GetSale/{id}.
        if (!await IsAuthorizedForSaleAsync(id))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Acceso denegado: no tiene permisos para consultar esta venta." });
        }

        try
        {
            var detail = await _salesService.GetSaleHistoryDetailAsync(id);
            return Ok(detail);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPut("{id}/price-list")]
    public async Task<ActionResult<SaleDto>> UpdatePriceList(int id, [FromBody] UpdatePriceListRequestDto request)
    {
        // 8.5-A3/8.6-B1: ownership a nivel de objeto.
        if (!await IsAuthorizedForSaleAsync(id))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Acceso denegado: no tiene permisos para modificar esta venta." });
        }

        try
        {
            var sale = await _salesService.UpdatePriceListAsync(id, request.PriceListType);
            return Ok(sale);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }
}