using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Attributes;
using Core.DTOs;
using Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sales.Module.DTOs;
using Sales.Module.Interfaces;

namespace Backend.API.Controllers;

[RequireSecurityStampValidation]
[Authorize(Roles = "Admin,Manager,Cashier")]
[ApiController]
[Route("api/[controller]")]
public class PaymentMethodsController : ControllerBase
{
    private readonly IPaymentMethodService _paymentService;
    private readonly IPaymentMethodFeeRuleService _paymentMethodFeeRuleService;
    private readonly ICurrentUserService _currentUserService;

    public PaymentMethodsController(
        IPaymentMethodService paymentService,
        IPaymentMethodFeeRuleService paymentMethodFeeRuleService,
        ICurrentUserService currentUserService)
    {
        ArgumentNullException.ThrowIfNull(paymentService);
        ArgumentNullException.ThrowIfNull(paymentMethodFeeRuleService);
        ArgumentNullException.ThrowIfNull(currentUserService);

        _paymentService = paymentService;
        _paymentMethodFeeRuleService = paymentMethodFeeRuleService;
        _currentUserService = currentUserService;
    }

    [HttpGet("active")]
    public async Task<IActionResult> GetActiveMethodsAsync(CancellationToken cancellationToken = default)
    {
        var methods = await _paymentService.GetActiveMethodsAsync(cancellationToken);
        return Ok(methods);
    }

    [HttpGet]
    public async Task<IActionResult> GetAllMethodsAsync(CancellationToken cancellationToken = default)
    {
        var methods = await _paymentService.GetAllAsync(cancellationToken);
        return Ok(methods);
    }

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

    /// <summary>
    /// P0 (S1/S5): alta opcionalmente atómica de un medio de pago con su comisión. La comisión es
    /// un costo del negocio (nunca se suma al cliente); efectivo y 0/0 no generan regla. Si la
    /// regla no se puede crear, el método recién creado se elimina para no dejar un alta parcial.
    /// </summary>
    [HttpPost]
    [Authorize(Roles = "Admin")]
    public async Task<IActionResult> CreateMethodAsync([FromBody] CreatePaymentMethodDto dto, CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
            return this.ApiValidationProblem(ModelState);

        var hasPositiveCommission = HasPositiveCommission(dto.Commission);

        // Cash has no acquirer cost: a positive commission is a contradictory request. Reject it
        // before persisting anything so no method is left behind.
        if (dto.IsCash && hasPositiveCommission)
        {
            return this.ApiBadRequest(
                "Un método de pago en efectivo no puede tener una comisión mayor que cero.");
        }

        var methodDto = new PaymentMethodDto
        {
            Name = dto.Name,
            RequiresReference = dto.RequiresReference,
            IsCash = dto.IsCash,
            DisplayOrder = dto.DisplayOrder,
            IsActive = dto.IsActive,
            // Vacío = derivar del nombre; una moneda explícita manda.
            Currency = dto.Currency ?? string.Empty
        };

        var created = await _paymentService.CreateAsync(methodDto, cancellationToken);

        if (!hasPositiveCommission)
        {
            return CreatedAtAction(nameof(GetMethodAsync), new { id = created.Id }, created);
        }

        try
        {
            await _paymentMethodFeeRuleService.CreateAsync(
                BuildFeeRuleRequest(created.Id, dto.Commission!),
                GetActorUserId(),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Compensating action: the method was persisted moments ago and has no history, so a
            // delete is safe. The original error is returned as a 4xx: never a method without the
            // requested commission.
            await RollbackCreatedMethodAsync(created.Id, cancellationToken);

            var status = exception is InvalidOperationException
                ? "Conflicto"
                : "Validación";
            Core.Logging.AppLogger.LogWarn(
                $"[PaymentMethods] Alta revertida al no poder crear la comisión: {exception.Message}",
                "PaymentMethods.CreateWithCommission");

            return exception is InvalidOperationException
                ? this.ApiConflict($"{status}: no se pudo crear la comisión del método de pago. {exception.Message}")
                : this.ApiBadRequest($"{status}: no se pudo crear la comisión del método de pago. {exception.Message}");
        }

        return CreatedAtAction(nameof(GetMethodAsync), new { id = created.Id }, created);
    }

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
                IsActive = dto.IsActive,
                // Vacío = conservar la moneda persistida (un renombre no la cambia).
                Currency = dto.Currency ?? string.Empty
            };

            var updated = await _paymentService.UpdateAsync(methodDto, cancellationToken);
            return Ok(updated);
        }
        catch (KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }

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

    private static bool HasPositiveCommission(PaymentMethodCommissionDto? commission)
    {
        return commission is not null
            && ((commission.Percentage ?? 0m) > 0m || (commission.FixedAmount ?? 0m) > 0m);
    }

    private static CreatePaymentMethodFeeRuleRequest BuildFeeRuleRequest(
        int paymentMethodId,
        PaymentMethodCommissionDto commission) => new()
    {
        PaymentMethodId = paymentMethodId,
        FeeType = commission.FeeType,
        Percentage = commission.Percentage,
        FixedAmount = commission.FixedAmount,
        CurrencyCode = commission.CurrencyCode,
        // An omitted/zero effective date means "from today at 00:00 UTC".
        EffectiveFromUtc = commission.EffectiveFromUtc == default
            ? DateTime.UtcNow.Date
            : commission.EffectiveFromUtc,
        EffectiveToUtc = commission.EffectiveToUtc,
        IsActive = commission.IsActive
    };

    private async Task RollbackCreatedMethodAsync(int paymentMethodId, CancellationToken cancellationToken)
    {
        try
        {
            await _paymentService.DeleteAsync(paymentMethodId, cancellationToken);
        }
        catch (Exception rollbackException)
        {
            // Never mask the original commission error; the residual is logged for audit.
            Core.Logging.AppLogger.LogCrash(
                rollbackException,
                "PaymentMethods.CreateWithCommission.Rollback");
        }
    }

    private int? GetActorUserId()
    {
        return _currentUserService.UserId != null && int.TryParse(_currentUserService.UserId, out int uid)
            ? uid
            : null;
    }
}
