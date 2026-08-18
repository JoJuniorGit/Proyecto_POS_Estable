using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sales.Module.Entities;
using Sales.Module.Interfaces;

namespace Backend.API.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class PaymentMethodsController : ControllerBase
{
    private readonly IPaymentMethodService _paymentService;

    public PaymentMethodsController(IPaymentMethodService paymentService)
    {
        _paymentService = paymentService;
    }

    [HttpGet("active")]
    public async Task<IActionResult> GetActiveMethods()
    {
        var methods = await _paymentService.GetActiveMethodsAsync();
        return Ok(methods);
    }

    [HttpGet]
    public async Task<IActionResult> GetAllMethods()
    {
        var methods = await _paymentService.GetAllAsync();
        return Ok(methods);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetMethod(int id)
    {
        try
        {
            var method = await _paymentService.GetByIdAsync(id);
            return Ok(method);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpPost]
    public async Task<IActionResult> CreateMethod([FromBody] PaymentMethod method)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var created = await _paymentService.CreateAsync(method);
        return CreatedAtAction(nameof(GetMethod), new { id = created.Id }, created);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> UpdateMethod(int id, [FromBody] PaymentMethod method)
    {
        if (id != method.Id)
            return BadRequest("ID mismatch");

        try
        {
            var updated = await _paymentService.UpdateAsync(method);
            return Ok(updated);
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteMethod(int id)
    {
        await _paymentService.DeleteAsync(id);
        return NoContent(); // Logical deletion applied
    }
}
