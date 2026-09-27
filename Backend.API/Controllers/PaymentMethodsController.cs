using Core.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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
    public async Task<IActionResult> GetActiveMethodsAsync(CancellationToken cancellationToken = default)
    {
        var methods = await _paymentService.GetActiveMethodsAsync(cancellationToken);
        return Ok(methods);
    }

    [NonAction]
    public Task<IActionResult> GetActiveMethods() => GetActiveMethodsAsync();

    [HttpGet]
    public async Task<IActionResult> GetAllMethodsAsync(CancellationToken cancellationToken = default)
    {
        var methods = await _paymentService.GetAllAsync(cancellationToken);
        return Ok(methods);
    }

    [NonAction]
    public Task<IActionResult> GetAllMethods() => GetAllMethodsAsync();

    [HttpGet("{id}")]
    public async Task<IActionResult> GetMethodAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            var method = await _paymentService.GetByIdAsync(id, cancellationToken);
            return Ok(method);
        }
        catch (KeyNotFoundException)
        {
            return this.ApiNotFound("Método de pago no encontrado.");
        }
    }

    [NonAction]
    public Task<IActionResult> GetMethod(int id) => GetMethodAsync(id);

    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateMethodAsync([FromBody] CreatePaymentMethodDto dto, CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
            return this.ApiValidationProblem(ModelState);

        var methodDto = new PaymentMethodDto
        {
            Name = dto.Name,
            RequiresReference = dto.RequiresReference,
            IsCash = dto.IsCash,
            DisplayOrder = dto.DisplayOrder,
            IsActive = dto.IsActive
        };

        var created = await _paymentService.CreateAsync(methodDto, cancellationToken);
        return CreatedAtAction(nameof(GetMethodAsync), new { id = created.Id }, created);
    }

    [NonAction]
    public Task<IActionResult> CreateMethod(CreatePaymentMethodDto dto) => CreateMethodAsync(dto);

    [HttpPut("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> UpdateMethodAsync(int id, [FromBody] UpdatePaymentMethodDto dto, CancellationToken cancellationToken = default)
    {
        if (id != dto.Id)
            return this.ApiBadRequest("El ID de la ruta no coincide con el ID del cuerpo de la petición.");

        if (!ModelState.IsValid)
            return this.ApiValidationProblem(ModelState);

        try
        {
            var methodDto = new PaymentMethodDto
            {
                Id = id,
                Name = dto.Name,
                RequiresReference = dto.RequiresReference,
                IsCash = dto.IsCash,
                DisplayOrder = dto.DisplayOrder,
                IsActive = dto.IsActive
            };

            var updated = await _paymentService.UpdateAsync(methodDto, cancellationToken);
            return Ok(updated);
        }
        catch (KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }

    [NonAction]
    public Task<IActionResult> UpdateMethod(int id, UpdatePaymentMethodDto dto) => UpdateMethodAsync(id, dto);

    [HttpDelete("{id}")]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> DeleteMethodAsync(int id, CancellationToken cancellationToken = default)
    {
        try
        {
            await _paymentService.DeleteAsync(id, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }

    [NonAction]
    public Task<IActionResult> DeleteMethod(int id) => DeleteMethodAsync(id);
}
