using System.Linq;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Core.DTOs;
using Sales.Module.DTOs;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

public partial class SalesController
{
    [Authorize(Roles = "Admin,Manager,Cashier")]
    [HttpGet("customers")]
    public async Task<ActionResult> GetCustomersAsync(
        [FromQuery] string? query = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool recentOnly = false,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var (items, totalCount) = await _salesService.GetCustomersAsync(query, page, pageSize, recentOnly, cancellationToken);
        Response.Headers["X-Total-Count"] = totalCount.ToString();
        return Ok(new CustomerPagedResultDto
        {
            Items = items.ToList(),
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize
        });
    }

    [NonAction]
    public Task<ActionResult> GetCustomers(
        [FromQuery] string? query = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] bool recentOnly = false) => GetCustomersAsync(query, page, pageSize, recentOnly);

    [HttpGet("customers/default")]
    public async Task<ActionResult<CustomerDto>> GetDefaultCustomerAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var customer = await _salesService.GetDefaultCustomerAsync(cancellationToken);
            return Ok(customer);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return this.ApiNotFound("Cliente por defecto no encontrado.");
        }
    }

    [NonAction]
    public Task<ActionResult<CustomerDto>> GetDefaultCustomer() => GetDefaultCustomerAsync();

    [HttpPut("{id}/customer")]
    public async Task<ActionResult<SaleDto>> UpdateSaleCustomerAsync(int id, [FromBody] UpdateSaleCustomerRequest request, CancellationToken cancellationToken = default)
    {
        if (!await IsAuthorizedForSaleAsync(id))
        {
            return this.ApiForbidden("Acceso denegado: no tiene permisos para modificar esta venta.");
        }

        try
        {
            var sale = await _salesService.UpdateSaleCustomerAsync(id, request.CustomerId, GetActorUserId(), cancellationToken);
            return Ok(sale);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }

    [NonAction]
    public Task<ActionResult<SaleDto>> UpdateSaleCustomer(int id, [FromBody] UpdateSaleCustomerRequest request) => UpdateSaleCustomerAsync(id, request);

    [HttpPost("customers")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<CustomerDto>> CreateCustomerAsync([FromBody] CreateCustomerDto request, CancellationToken cancellationToken = default)
    {
        var customer = await _salesService.CreateCustomerAsync(request, cancellationToken);
        return Ok(customer);
    }

    [NonAction]
    public Task<ActionResult<CustomerDto>> CreateCustomer([FromBody] CreateCustomerDto request) => CreateCustomerAsync(request);

    [HttpPut("customers/{id}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult<CustomerDto>> UpdateCustomerAsync(int id, [FromBody] UpdateCustomerDto request, CancellationToken cancellationToken = default)
    {
        try
        {
            var customer = await _salesService.UpdateCustomerAsync(id, request, cancellationToken);
            return Ok(customer);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }

    [NonAction]
    public Task<ActionResult<CustomerDto>> UpdateCustomer(int id, [FromBody] UpdateCustomerDto request) => UpdateCustomerAsync(id, request);

    [HttpDelete("customers/{id}")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<ActionResult> DeleteCustomerAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            await _salesService.DeleteCustomerAsync(id, cancellationToken);
            return NoContent();
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }

    [NonAction]
    public Task<ActionResult> DeleteCustomer(int id) => DeleteCustomerAsync(id);
}