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

    [Authorize(Roles = "Admin,Manager,Cashier")]
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

    [HttpPut("{id}/customer")]
    public async Task<ActionResult<SaleDto>> UpdateSaleCustomerAsync(int id, [FromBody] UpdateSaleCustomerRequest request, CancellationToken cancellationToken = default)
    {
        if (!await IsAuthorizedForSaleAsync(id, cancellationToken))
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

    /// <summary>
    /// 8.188: el alta de cliente es una operación de contacto (cédula/RIF, nombre, teléfono) y
    /// queda disponible para caja. Los campos crediticios del payload se descartan server-side
    /// para TODOS los roles: la línea se administra únicamente por
    /// <c>PUT customers/{id}/credit-profile</c> (Admin/Manager).
    /// </summary>
    [HttpPost("customers")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<ActionResult<CustomerDto>> CreateCustomerAsync([FromBody] CreateCustomerDto request, CancellationToken cancellationToken = default)
    {
        var customer = await _salesService.CreateCustomerAsync(request, cancellationToken);
        return Ok(customer);
    }

    /// <summary>
    /// 8.188: edición de contacto (cédula/RIF, nombre y teléfono) habilitada para caja. Los
    /// parámetros crediticios del payload se ignoran server-side para TODOS los roles, incluso
    /// Admin/Manager: el crédito solo cambia por <c>PUT credit-profile</c>.
    /// </summary>
    [HttpPut("customers/{id}")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
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

    /// <summary>
    /// 8.188: la eliminación física sigue siendo Admin/Manager. El servicio la bloquea con un
    /// error de dominio si el cliente posee cualquier rastro de cartera (saldo o historial de
    /// cuentas por cobrar) o abonos registrados; el FK Restrict es la defensa final.
    /// </summary>
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
}