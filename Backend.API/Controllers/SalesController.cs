using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Core.DTOs;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System.Linq;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class SalesController : ControllerBase
{
    private readonly ISalesService _salesService;
    private readonly Core.Interfaces.ICurrentUserService _currentUserService;

    public SalesController(ISalesService salesService, Core.Interfaces.ICurrentUserService currentUserService)
    {
        _salesService = salesService;
        _currentUserService = currentUserService;
    }

    [HttpPost("start")]
    public async Task<ActionResult<SaleDto>> StartSale([FromQuery] int? cashierId = null)
    {
        int? effectiveCashierId = _currentUserService.UserId != null && int.TryParse(_currentUserService.UserId, out int uid) 
            ? uid 
            : cashierId;

        var _sale = await _salesService.StartSaleAsync(effectiveCashierId);
        return Ok(_sale);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<SaleDto>> GetSale(int id)
    {
        try
        {
            var _sale = await _salesService.GetSaleAsync(id);
            return Ok(_sale);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost("{id}/items")]
    public async Task<ActionResult<SaleDto>> AddItem(int id, [FromBody] AddItemRequest request)
    {
        try
        {
            var _sale = await _salesService.AddItemAsync(id, request.ProductId, request.Quantity, request.ExchangeRate, request.CustomUnitPriceUsd, request.CustomUnitPriceLocal);
            return Ok(_sale);
        }
        catch (System.Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("{id}/items/{itemId}")]
    public async Task<ActionResult<SaleDto>> RemoveItem(int id, int itemId, [FromQuery] decimal exchangeRate)
    {
        try
        {
            var _sale = await _salesService.RemoveItemAsync(id, itemId, exchangeRate);
            return Ok(_sale);
        }
        catch (System.Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPut("{id}/items/{itemId}")]
    public async Task<ActionResult<SaleDto>> UpdateItemQuantity(int id, int itemId, [FromBody] UpdateQuantityRequest request)
    {
        try
        {
            var _sale = await _salesService.UpdateItemQuantityAsync(id, itemId, request.Quantity, request.ExchangeRate);
            return Ok(_sale);
        }
        catch (System.Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPut("{id}/exchange-rate")]
    public async Task<ActionResult<SaleDto>> UpdateExchangeRate(int id, [FromQuery] decimal exchangeRate)
    {
        try
        {
            var _sale = await _salesService.UpdateExchangeRateAsync(id, exchangeRate);
            return Ok(_sale);
        }
        catch (System.Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("{id}/hold")]
    public async Task<ActionResult<SaleDto>> HoldSale(int id, [FromBody] HoldSaleRequestDto request)
    {
        try
        {
            var _sale = await _salesService.HoldSaleAsync(id, request);
            return Ok(_sale);
        }
        catch (System.Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPut("{id}/items")]
    public async Task<ActionResult<SaleDto>> UpdateSaleItems(int id, [FromBody] UpdateSaleItemsRequestDto request)
    {
        try
        {
            var _sale = await _salesService.UpdateSaleItemsAsync(id, request);
            return Ok(_sale);
        }
        catch (System.Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("{id}/payments")]
    public async Task<ActionResult<SaleDto>> AddPayment(int id, [FromBody] AddPaymentRequestDto request)
    {
        try
        {
            var _sale = await _salesService.AddPaymentToHoldSaleAsync(id, request);
            return Ok(_sale);
        }
        catch (System.Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("pending")]
    public async Task<ActionResult<System.Collections.Generic.IEnumerable<SaleDto>>> GetPendingSales()
    {
        var _pending = await _salesService.GetPendingSalesAsync();
        return Ok(_pending);
    }

    [HttpPost("{id}/cancel")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> CancelSale(int id)
    {
        try
        {
            await _salesService.CancelSaleAsync(id);
            return Ok(new { message = $"Pedido #{id} anulado exitosamente." });
        }
        catch (System.InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (System.Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

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
            var _customer = await _salesService.GetDefaultCustomerAsync();
            return Ok(_customer);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPut("{id}/customer")]
    public async Task<ActionResult<SaleDto>> UpdateSaleCustomer(int id, [FromBody] UpdateSaleCustomerRequest request)
    {
        try
        {
            var _sale = await _salesService.UpdateSaleCustomerAsync(id, request.CustomerId);
            return Ok(_sale);
        }
        catch (System.Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("customers")]
    public async Task<ActionResult<CustomerDto>> CreateCustomer([FromBody] CreateCustomerDto request)
    {
        try
        {
            var _customer = await _salesService.CreateCustomerAsync(request);
            return Ok(_customer);
        }
        catch (System.Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPut("customers/{id}")]
    public async Task<ActionResult<CustomerDto>> UpdateCustomer(int id, [FromBody] UpdateCustomerDto request)
    {
        try
        {
            var _customer = await _salesService.UpdateCustomerAsync(id, request);
            return Ok(_customer);
        }
        catch (System.Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpDelete("customers/{id}")]
    public async Task<ActionResult> DeleteCustomer(int id)
    {
        try
        {
            await _salesService.DeleteCustomerAsync(id);
            return NoContent();
        }
        catch (System.Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }


    [HttpPost("{id}/complete")]
    public async Task<ActionResult> CompleteSale(int id, [FromBody] CompleteSaleRequest request)
    {
        try
        {
            int? effectiveCashierId = _currentUserService.UserId != null && int.TryParse(_currentUserService.UserId, out int uid)
                ? uid
                : request.CashierId;

            var _payment_infos = request.Payments.Select(p => new PaymentInfo(p.PaymentMethodId, p.Amount, p.AmountBsS > 0 ? p.AmountBsS : p.AmountLocal, p.ReferenceNumber));
            int _real_id = await _salesService.CompleteSaleAsync(id, request.ExchangeRate, _payment_infos, request.RoundingAdjustment, effectiveCashierId, request.IsPendingPickup);
            return Ok(_real_id);
        }
        catch (System.Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpPost("{id}/confirm-pickup")]
    public async Task<ActionResult<SaleHistoryDto>> ConfirmPickup(int id)
    {
        try
        {
            var _sale = await _salesService.ConfirmPickupAsync(id);
            return Ok(_sale);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return NotFound();
        }
        catch (System.Exception ex)
        {
            return BadRequest(ex.Message);
        }
    }

    [HttpGet("pending-pickups")]
    public async Task<ActionResult<System.Collections.Generic.IEnumerable<PendingPickupDto>>> GetPendingPickups()
    {
        var _pending = await _salesService.GetPendingPickupsAsync();
        return Ok(_pending);
    }

    [HttpGet("history")]
    public async Task<ActionResult> GetHistory([FromQuery] int page = 1, [FromQuery] int pageSize = 20, [FromQuery] System.DateTime? startDate = null, [FromQuery] System.DateTime? endDate = null, [FromQuery] string? search = null)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var (_items, _total_count) = await _salesService.GetSalesHistoryAsync(page, pageSize, startDate, endDate, search);
        return Ok(new { Items = _items, TotalCount = _total_count });
    }

    [HttpGet("{id}/history-detail")]
    public async Task<ActionResult> GetHistoryDetail(int id)
    {
        try
        {
            var _detail = await _salesService.GetSaleHistoryDetailAsync(id);
            return Ok(_detail);
        }
        catch (System.Collections.Generic.KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPut("{id}/price-list")]
    public async Task<ActionResult<SaleDto>> UpdatePriceList(int id, [FromBody] UpdatePriceListRequestDto request)
    {
        try
        {
            var sale = await _salesService.UpdatePriceListAsync(id, request.PriceListType);
            return Ok(sale);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return NotFound(new { Message = ex.Message });
        }
        catch (System.ArgumentException ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
        catch (System.InvalidOperationException ex)
        {
            return BadRequest(new { Message = ex.Message });
        }
    }
}

public class AddItemRequest
{
    public int ProductId { get; set; }
    public decimal Quantity { get; set; }
    public decimal ExchangeRate { get; set; }
    public decimal? CustomUnitPriceUsd { get; set; }
    public decimal? CustomUnitPriceLocal { get; set; }
}

public class UpdateQuantityRequest
{
    public decimal Quantity { get; set; }
    public decimal ExchangeRate { get; set; }
}

public class CompleteSaleRequest
{
    public decimal ExchangeRate { get; set; }
    public decimal RoundingAdjustment { get; set; }
    public int? CashierId { get; set; }
    public bool IsPendingPickup { get; set; } = false;
    public IEnumerable<SalePaymentDto> Payments { get; set; } = new List<SalePaymentDto>();
}
public class UpdateSaleCustomerRequest
{
    public int CustomerId { get; set; }
}
