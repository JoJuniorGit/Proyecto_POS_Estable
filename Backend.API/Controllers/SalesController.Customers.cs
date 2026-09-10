using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Core.DTOs;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

public partial class SalesController
{
    [HttpGet("customers")]
    public async Task<ActionResult> GetCustomers(
        [FromQuery] string? query = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool recentOnly = false)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var (items, totalCount) = await _salesService.GetCustomersAsync(query, page, pageSize, recentOnly);
        Response.Headers["X-Total-Count"] = totalCount.ToString();
        return Ok(new { items, totalCount, page, pageSize });
    }

    [HttpGet("customers/default")]
    public async Task<ActionResult<CustomerDto>> GetDefaultCustomer()
    {
        try
        {
            var customer = await _salesService.GetDefaultCustomerAsync();
            return Ok(customer);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPut("{id}/customer")]
    public async Task<ActionResult<SaleDto>> UpdateSaleCustomer(int id, [FromBody] UpdateSaleCustomerRequest request)
    {
        // 8.5-A3/8.6-B1: ownership a nivel de objeto.
        if (!await IsAuthorizedForSaleAsync(id))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Acceso denegado: no tiene permisos para modificar esta venta." });
        }

        try
        {
            var sale = await _salesService.UpdateSaleCustomerAsync(id, request.CustomerId);
            return Ok(sale);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }

    [HttpPost("customers")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<CustomerDto>> CreateCustomer([FromBody] CreateCustomerDto request)
    {
        var customer = await _salesService.CreateCustomerAsync(request);
        return Ok(customer);
    }

    [HttpPut("customers/{id}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<CustomerDto>> UpdateCustomer(int id, [FromBody] UpdateCustomerDto request)
    {
        try
        {
            var customer = await _salesService.UpdateCustomerAsync(id, request);
            return Ok(customer);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }

    [HttpDelete("customers/{id}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult> DeleteCustomer(int id)
    {
        try
        {
            await _salesService.DeleteCustomerAsync(id);
            return NoContent();
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }
}