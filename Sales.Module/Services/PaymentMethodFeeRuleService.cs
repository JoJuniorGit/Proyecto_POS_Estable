using Core.Common;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

/// <summary>
/// Paso 14 (F3-S1): administración de las reglas de comisión de los medios de pago. Valida que
/// el método exista, no sea efectivo y que la vigencia no se solape con otra regla activa; la
/// moneda debe ser USD o la moneda local del régimen vigente. La baja es lógica para preservar
/// la referencia histórica de los cobros y sus snapshots de comisión.
/// </summary>
public sealed class PaymentMethodFeeRuleService : IPaymentMethodFeeRuleService
{
    private const string UsdCurrencyCode = "USD";

    private readonly SalesDbContext _context;
    private readonly IMonetaryProfileService _monetaryProfileService;

    public PaymentMethodFeeRuleService(
        SalesDbContext context,
        IMonetaryProfileService monetaryProfileService)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(monetaryProfileService);

        _context = context;
        _monetaryProfileService = monetaryProfileService;
    }

    public async Task<IReadOnlyList<PaymentMethodFeeRuleDto>> GetAllAsync(
        int? paymentMethodId,
        bool? activeOnly,
        CancellationToken cancellationToken = default)
    {
        IQueryable<PaymentMethodFeeRule> query = _context.PaymentMethodFeeRules
            .AsNoTracking()
            .Include(rule => rule.PaymentMethod);

        if (paymentMethodId.HasValue)
        {
            query = query.Where(rule => rule.PaymentMethodId == paymentMethodId.Value);
        }

        // activeOnly is a filter switch, not a tri-state view: false/null keep the full
        // administrative history (including soft-deleted rules) visible.
        if (activeOnly == true)
        {
            query = query.Where(rule => rule.IsActive);
        }

        var rules = await query
            .OrderBy(rule => rule.PaymentMethodId)
            .ThenByDescending(rule => rule.EffectiveFromUtc)
            .ThenBy(rule => rule.Id)
            .ToListAsync(cancellationToken);

        return rules.Select(rule => ToDto(rule, rule.PaymentMethod?.Name ?? string.Empty)).ToList();
    }

    public async Task<PaymentMethodFeeRuleDto> GetByIdAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        var rule = await _context.PaymentMethodFeeRules
            .AsNoTracking()
            .Include(entity => entity.PaymentMethod)
            .FirstOrDefaultAsync(entity => entity.Id == id, cancellationToken);

        if (rule is null)
        {
            throw new KeyNotFoundException($"La regla de comisión con ID {id} no existe.");
        }

        return ToDto(rule, rule.PaymentMethod?.Name ?? string.Empty);
    }

    public async Task<PaymentMethodFeeRuleDto> CreateAsync(
        CreatePaymentMethodFeeRuleRequest request,
        int? actingUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var method = await ResolveNonCashMethodAsync(request.PaymentMethodId, cancellationToken);
        var effectiveFromUtc = NormalizeUtc(request.EffectiveFromUtc);
        DateTime? effectiveToUtc = request.EffectiveToUtc.HasValue
            ? NormalizeUtc(request.EffectiveToUtc.Value)
            : null;
        var (percentage, fixedAmount) = ValidateFeeAmounts(
            request.FeeType,
            request.Percentage,
            request.FixedAmount);
        ValidateEffectiveRange(effectiveFromUtc, effectiveToUtc);
        var currencyCode = await ResolveCurrencyCodeAsync(request.CurrencyCode, cancellationToken);
        await EnsureNoOverlapAsync(
            request.PaymentMethodId,
            effectiveFromUtc,
            effectiveToUtc,
            request.IsActive,
            excludeRuleId: null,
            cancellationToken);

        var now = DateTime.UtcNow;
        var entity = new PaymentMethodFeeRule
        {
            PaymentMethodId = request.PaymentMethodId,
            FeeType = request.FeeType,
            Percentage = percentage,
            FixedAmount = fixedAmount,
            CurrencyCode = currencyCode,
            EffectiveFromUtc = effectiveFromUtc,
            EffectiveToUtc = effectiveToUtc,
            IsActive = request.IsActive,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            CreatedByUserId = actingUserId,
            UpdatedByUserId = actingUserId
        };

        _context.PaymentMethodFeeRules.Add(entity);
        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(entity, method.Name);
    }

    public async Task<PaymentMethodFeeRuleDto> UpdateAsync(
        int id,
        UpdatePaymentMethodFeeRuleRequest request,
        int? actingUserId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var entity = await _context.PaymentMethodFeeRules
            .FirstOrDefaultAsync(rule => rule.Id == id, cancellationToken);

        if (entity is null)
        {
            throw new KeyNotFoundException($"La regla de comisión con ID {id} no existe.");
        }

        // The payment method is immutable on update, so it is re-validated but never replaced.
        var method = await ResolveNonCashMethodAsync(entity.PaymentMethodId, cancellationToken);
        var effectiveFromUtc = NormalizeUtc(request.EffectiveFromUtc);
        DateTime? effectiveToUtc = request.EffectiveToUtc.HasValue
            ? NormalizeUtc(request.EffectiveToUtc.Value)
            : null;
        var (percentage, fixedAmount) = ValidateFeeAmounts(
            request.FeeType,
            request.Percentage,
            request.FixedAmount);
        ValidateEffectiveRange(effectiveFromUtc, effectiveToUtc);
        var currencyCode = await ResolveCurrencyCodeAsync(request.CurrencyCode, cancellationToken);
        await EnsureNoOverlapAsync(
            entity.PaymentMethodId,
            effectiveFromUtc,
            effectiveToUtc,
            request.IsActive,
            entity.Id,
            cancellationToken);

        entity.FeeType = request.FeeType;
        entity.Percentage = percentage;
        entity.FixedAmount = fixedAmount;
        entity.CurrencyCode = currencyCode;
        entity.EffectiveFromUtc = effectiveFromUtc;
        entity.EffectiveToUtc = effectiveToUtc;
        entity.IsActive = request.IsActive;
        entity.UpdatedAtUtc = DateTime.UtcNow;
        entity.UpdatedByUserId = actingUserId;

        await _context.SaveChangesAsync(cancellationToken);

        return ToDto(entity, method.Name);
    }

    public async Task DeleteAsync(
        int id,
        CancellationToken cancellationToken = default)
    {
        var entity = await _context.PaymentMethodFeeRules
            .FirstOrDefaultAsync(rule => rule.Id == id, cancellationToken);

        if (entity is null)
        {
            throw new KeyNotFoundException($"La regla de comisión con ID {id} no existe.");
        }

        // Soft delete: SalePayment/ReceivablePayment rows keep referencing the rule, so a hard
        // delete would break the historical commission trail.
        entity.IsActive = false;
        entity.UpdatedAtUtc = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<PaymentMethod> ResolveNonCashMethodAsync(
        int paymentMethodId,
        CancellationToken cancellationToken)
    {
        var method = await _context.PaymentMethods
            .AsNoTracking()
            .FirstOrDefaultAsync(
                entity => entity.Id == paymentMethodId && !entity.IsDeleted,
                cancellationToken);

        if (method is null)
        {
            throw new KeyNotFoundException(
                $"El método de pago con ID {paymentMethodId} no existe o está eliminado.");
        }

        if (method.IsCash)
        {
            throw new ArgumentException(
                "No se puede configurar una comisión para un método de pago en efectivo.");
        }

        return method;
    }

    private static (decimal? Percentage, decimal? FixedAmount) ValidateFeeAmounts(
        PaymentMethodFeeType feeType,
        decimal? percentage,
        decimal? fixedAmount)
    {
        if (!Enum.IsDefined(feeType))
        {
            throw new ArgumentException($"El tipo de comisión '{feeType}' no es válido.");
        }

        switch (feeType)
        {
            case PaymentMethodFeeType.Percentage:
                if (percentage is null || percentage <= 0m || percentage > 100m)
                {
                    throw new ArgumentException(
                        "El porcentaje de comisión debe ser mayor que 0 y menor o igual a 100.");
                }

                // Irrelevant components are normalized to null instead of persisting leftovers.
                return (percentage, null);

            case PaymentMethodFeeType.Fixed:
                if (fixedAmount is null || fixedAmount <= 0m)
                {
                    throw new ArgumentException(
                        "El monto fijo de comisión debe ser mayor que 0.");
                }

                return (null, fixedAmount);

            case PaymentMethodFeeType.PercentagePlusFixed:
                if (percentage is null || percentage <= 0m || percentage > 100m)
                {
                    throw new ArgumentException(
                        "El porcentaje de comisión debe ser mayor que 0 y menor o igual a 100.");
                }

                if (fixedAmount is < 0m)
                {
                    throw new ArgumentException(
                        "El monto fijo de comisión no puede ser negativo.");
                }

                if (percentage <= 0m && (fixedAmount ?? 0m) <= 0m)
                {
                    throw new ArgumentException(
                        "La regla de comisión debe tener al menos un componente mayor que 0.");
                }

                return (percentage, fixedAmount ?? 0m);

            default:
                throw new ArgumentException($"El tipo de comisión '{feeType}' no es válido.");
        }
    }

    private static void ValidateEffectiveRange(DateTime effectiveFromUtc, DateTime? effectiveToUtc)
    {
        if (effectiveToUtc.HasValue && effectiveFromUtc >= effectiveToUtc.Value)
        {
            throw new ArgumentException(
                "La fecha de inicio de vigencia debe ser anterior a la fecha de fin de vigencia.");
        }
    }

    private async Task<string> ResolveCurrencyCodeAsync(
        string currencyCode,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currencyCode))
        {
            throw new ArgumentException("La moneda de la comisión es obligatoria.");
        }

        var normalizedCode = CurrencyCodeNormalizer.NormalizeToIso(currencyCode);
        if (normalizedCode is null)
        {
            throw new ArgumentException(
                $"La moneda '{currencyCode}' no es un código ISO 4217 válido para la comisión.");
        }

        var profile = await _monetaryProfileService.GetAsync(cancellationToken);
        var localCurrencyCode = PaymentMethodFeeResolver.ResolveLocalCurrencyCode(profile);
        var normalizedLocalCode = CurrencyCodeNormalizer.NormalizeToIso(localCurrencyCode)
            ?? localCurrencyCode;

        if (!string.Equals(normalizedCode, UsdCurrencyCode, StringComparison.Ordinal)
            && !string.Equals(normalizedCode, normalizedLocalCode, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"La moneda '{currencyCode}' no es compatible con el régimen monetario vigente; use USD o la moneda local '{localCurrencyCode}'.");
        }

        return normalizedCode;
    }

    private async Task EnsureNoOverlapAsync(
        int paymentMethodId,
        DateTime effectiveFromUtc,
        DateTime? effectiveToUtc,
        bool isActive,
        int? excludeRuleId,
        CancellationToken cancellationToken)
    {
        // Windows are only exclusive among active rules: an inactive rule is history and cannot
        // block a new configuration.
        if (!isActive)
        {
            return;
        }

        var endUtc = effectiveToUtc;
        var excludedId = excludeRuleId;
        var overlaps = await _context.PaymentMethodFeeRules.AnyAsync(
            rule => rule.PaymentMethodId == paymentMethodId
                && rule.IsActive
                && (!excludedId.HasValue || rule.Id != excludedId.Value)
                && (endUtc == null || rule.EffectiveFromUtc < endUtc.Value)
                && (rule.EffectiveToUtc == null || rule.EffectiveToUtc > effectiveFromUtc),
            cancellationToken);

        if (overlaps)
        {
            throw new InvalidOperationException(
                "Ya existe una regla de comisión activa para este método de pago que se solapa con la vigencia indicada.");
        }
    }

    private static DateTime NormalizeUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };

    private static PaymentMethodFeeRuleDto ToDto(PaymentMethodFeeRule rule, string paymentMethodName) => new()
    {
        Id = rule.Id,
        PaymentMethodId = rule.PaymentMethodId,
        PaymentMethodName = paymentMethodName,
        FeeType = rule.FeeType,
        Percentage = rule.Percentage,
        FixedAmount = rule.FixedAmount,
        CurrencyCode = rule.CurrencyCode,
        EffectiveFromUtc = rule.EffectiveFromUtc,
        EffectiveToUtc = rule.EffectiveToUtc,
        IsActive = rule.IsActive,
        CreatedAtUtc = rule.CreatedAtUtc,
        UpdatedAtUtc = rule.UpdatedAtUtc
    };
}
