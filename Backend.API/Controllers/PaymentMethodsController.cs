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
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateMethod([FromBody] PaymentMethod method)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var created = await _paymentService.CreateAsync(method);
            return CreatedAtAction(nameof(GetMethod), new { id = created.Id }, created);
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateMethod(int id, [FromBody] PaymentMethod method)
    {
        if (id != method.Id)
            return BadRequest(new { message = "ID mismatch" });

        try
        {
            var updated = await _paymentService.UpdateAsync(method);
            return Ok(updated);
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (ArgumentException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteMethod(int id)
    {
        try
        {
            await _paymentService.DeleteAsync(id);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return NotFound(new { message = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return Conflict(new { message = ex.Message });
        }
    }
}
