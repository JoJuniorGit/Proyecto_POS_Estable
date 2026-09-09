using Backend.API.DTOs;
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
        return Ok(methods.Select(ToDto));
    }

    [HttpGet]
    public async Task<IActionResult> GetAllMethods()
    {
        var methods = await _paymentService.GetAllAsync();
        var dtos = methods.Select(ToDto).ToList();
        return Ok(dtos);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetMethod(int id)
    {
        try
        {
            var method = await _paymentService.GetByIdAsync(id);
            return Ok(ToDto(method));
        }
        catch (KeyNotFoundException)
        {
            return NotFound();
        }
    }

    /// <summary>
    /// Crea un nuevo método de pago mediante DTO protegido ([8B-CR1]).
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateMethod([FromBody] CreatePaymentMethodDto dto)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var method = new PaymentMethod
            {
                Name = dto.Name,
                RequiresReference = dto.RequiresReference,
                IsCash = dto.IsCash,
                DisplayOrder = dto.DisplayOrder,
                IsActive = dto.IsActive
            };

            var created = await _paymentService.CreateAsync(method);
            return CreatedAtAction(nameof(GetMethod), new { id = created.Id }, ToDto(created));
        }
        catch (ArgumentException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Actualiza un método de pago existente mediante DTO protegido ([8B-CR1]).
    /// </summary>
    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateMethod(int id, [FromBody] UpdatePaymentMethodDto dto)
    {
        if (id != dto.Id)
            return BadRequest(new { message = "ID mismatch" });

        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        try
        {
            var method = new PaymentMethod
            {
                Id = id,
                Name = dto.Name,
                RequiresReference = dto.RequiresReference,
                IsCash = dto.IsCash,
                DisplayOrder = dto.DisplayOrder,
                IsActive = dto.IsActive
            };

            var updated = await _paymentService.UpdateAsync(method);
            return Ok(ToDto(updated));
        }
        catch (KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
        catch (ArgumentException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return this.ApiConflict(ex.Message);
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
            return this.ApiNotFound(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return this.ApiConflict(ex.Message);
        }
    }

    private static PaymentMethodDto ToDto(PaymentMethod method)
    {
        return new PaymentMethodDto
        {
            Id = method.Id,
            Name = method.Name,
            IsActive = method.IsActive,
            RequiresReference = method.RequiresReference,
            IsCash = method.IsCash,
            Currency = method.Currency,
            DisplayOrder = method.DisplayOrder,
            IsDeleted = method.IsDeleted
        };
    }
}
