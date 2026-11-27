using Backend.API.Attributes;
using Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sales.Module.DTOs;
using Sales.Module.Interfaces;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

/// <summary>
/// Paso 14 (F3-S1): CRUD de reglas de comisión de medios de pago. Exclusivo Admin: el Manager
/// puede leer rentabilidad pero no alterar la configuración de comisiones. La baja es lógica
/// para preservar los snapshots de los cobros ya realizados.
/// </summary>
[ApiController]
[Authorize(Policy = "RequireAdmin")]
[RequireSecurityStampValidation]
[Route("api/payment-method-fees")]
public sealed class PaymentMethodFeesController : ControllerBase
{
    private readonly IPaymentMethodFeeRuleService _paymentMethodFeeRuleService;
    private readonly ICurrentUserService _currentUserService;

    public PaymentMethodFeesController(
        IPaymentMethodFeeRuleService paymentMethodFeeRuleService,
        ICurrentUserService currentUserService)
    {
        ArgumentNullException.ThrowIfNull(paymentMethodFeeRuleService);
        ArgumentNullException.ThrowIfNull(currentUserService);

        _paymentMethodFeeRuleService = paymentMethodFeeRuleService;
        _currentUserService = currentUserService;
    }

    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<PaymentMethodFeeRuleDto>>> GetAllAsync(
        [FromQuery] int? paymentMethodId = null,
        [FromQuery] bool? activeOnly = null,
        CancellationToken cancellationToken = default)
    {
        var rules = await _paymentMethodFeeRuleService.GetAllAsync(
            paymentMethodId,
            activeOnly,
            cancellationToken);
        return Ok(rules);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<PaymentMethodFeeRuleDto>> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var rule = await _paymentMethodFeeRuleService.GetByIdAsync(id, cancellationToken);
            return Ok(rule);
        }
        catch (KeyNotFoundException exception)
        {
            return this.ApiNotFound(exception.Message);
        }
    }

    [HttpPost]
    public async Task<IActionResult> CreateAsync(
        [FromBody] CreatePaymentMethodFeeRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return this.ApiValidationProblem(ModelState);
        }

        try
        {
            var created = await _paymentMethodFeeRuleService.CreateAsync(
                request,
                GetActorUserId(),
                cancellationToken);
            return CreatedAtAction(nameof(GetByIdAsync), new { id = created.Id }, created);
        }
        catch (KeyNotFoundException exception)
        {
            return this.ApiNotFound(exception.Message);
        }
        catch (ArgumentException exception)
        {
            return this.ApiBadRequest(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return this.ApiBadRequest(exception.Message);
        }
    }

    [HttpPut("{id}")]
    public async Task<ActionResult<PaymentMethodFeeRuleDto>> UpdateAsync(
        int id,
        [FromBody] UpdatePaymentMethodFeeRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return this.ApiValidationProblem(ModelState);
        }

        try
        {
            var updated = await _paymentMethodFeeRuleService.UpdateAsync(
                id,
                request,
                GetActorUserId(),
                cancellationToken);
            return Ok(updated);
        }
        catch (KeyNotFoundException exception)
        {
            return this.ApiNotFound(exception.Message);
        }
        catch (ArgumentException exception)
        {
            return this.ApiBadRequest(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return this.ApiBadRequest(exception.Message);
        }
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> DeleteAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _paymentMethodFeeRuleService.DeleteAsync(id, cancellationToken);
            return NoContent();
        }
        catch (KeyNotFoundException exception)
        {
            return this.ApiNotFound(exception.Message);
        }
    }

    private int? GetActorUserId()
    {
        return _currentUserService.UserId != null && int.TryParse(_currentUserService.UserId, out int uid)
            ? uid
            : null;
    }
}
