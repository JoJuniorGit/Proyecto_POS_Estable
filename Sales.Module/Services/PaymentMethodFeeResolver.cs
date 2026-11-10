using Core.Common;
using Core.DTOs;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

/// <summary>
/// Paso 14 (S7): snapshot inmutable de la regla de comision de un medio de pago. La comision es
/// un costo del negocio y nunca se carga al cliente: solo alimenta el calculo de beneficios.
/// <see cref="None"/> representa "sin regla activa" (montos 0) y no debe re-resolverse nunca.
/// </summary>
public sealed record PaymentMethodFeeSnapshot(
    int? RuleId,
    PaymentMethodFeeType? FeeType,
    decimal? FeePercentage,
    decimal? FeeFixedAmount,
    string? FeeCurrencyCode,
    decimal FeeAmountUsd,
    decimal FeeAmountLocal,
    decimal FeeRate,
    bool FeeConfigured,
    DateTime? FeeRuleEffectiveFromUtc)
{
    public static PaymentMethodFeeSnapshot None { get; } = new(
        RuleId: null,
        FeeType: null,
        FeePercentage: null,
        FeeFixedAmount: null,
        FeeCurrencyCode: null,
        FeeAmountUsd: 0m,
        FeeAmountLocal: 0m,
        FeeRate: 0m,
        FeeConfigured: false,
        FeeRuleEffectiveFromUtc: null);
}

/// <summary>
/// Paso 14 (S7): resuelve la regla de comision vigente para un cobro. El resultado se congela en
/// el pago (<see cref="SalePayment"/> o <see cref="ReceivablePayment"/>) al crearse; las reglas
/// historicas no se re-leen. La conversion usa la tasa snapshot del pago: USD conserva 4dp y la
/// moneda local 2dp, sin alterar jamas el monto cobrado al cliente.
/// </summary>
public static class PaymentMethodFeeResolver
{
    private const string UsdCurrencyCode = "USD";

    public static async Task<PaymentMethodFeeSnapshot> ResolveAsync(
        SalesDbContext context,
        int paymentMethodId,
        DateTime paymentDateUtc,
        decimal amountUsd,
        decimal amountLocal,
        decimal rate,
        string localCurrencyCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var paymentMethod = await context.PaymentMethods
            .AsNoTracking()
            .FirstOrDefaultAsync(
                method => method.Id == paymentMethodId && !method.IsDeleted,
                cancellationToken);

        if (paymentMethod is null)
        {
            throw new InvalidOperationException(
                $"No se encontró el método de pago (Id={paymentMethodId}) configurado para resolver la comisión. Verifique la configuración de métodos de pago.");
        }

        // El efectivo nunca genera comision, aunque exista una regla configurada por error.
        if (paymentMethod.IsCash)
        {
            return PaymentMethodFeeSnapshot.None;
        }

        var activeRules = await context.PaymentMethodFeeRules
            .AsNoTracking()
            .Where(rule => rule.PaymentMethodId == paymentMethodId
                && rule.IsActive
                && rule.EffectiveFromUtc <= paymentDateUtc
                && (rule.EffectiveToUtc == null || rule.EffectiveToUtc > paymentDateUtc))
            .ToListAsync(cancellationToken);

        if (activeRules.Count == 0)
        {
            return PaymentMethodFeeSnapshot.None;
        }

        if (activeRules.Count > 1)
        {
            throw new InvalidOperationException(
                $"Hay más de una regla de comisión activa para el método de pago (Id={paymentMethodId}) en la fecha {paymentDateUtc:O}. Corrija las vigencias configuradas.");
        }

        var rule = activeRules[0];
        string ruleCurrencyCode = CurrencyCodeNormalizer.NormalizeToIso(rule.CurrencyCode) ?? rule.CurrencyCode;
        string normalizedLocalCurrencyCode = CurrencyCodeNormalizer.NormalizeToIso(localCurrencyCode) ?? localCurrencyCode;

        bool usesUsd = string.Equals(ruleCurrencyCode, UsdCurrencyCode, StringComparison.Ordinal);
        bool usesLocal = string.Equals(ruleCurrencyCode, normalizedLocalCurrencyCode, StringComparison.Ordinal);
        if (!usesUsd && !usesLocal)
        {
            throw new InvalidOperationException(
                $"La regla de comisión (Id={rule.Id}) usa la moneda '{rule.CurrencyCode}', pero solo se admite USD o la moneda local del régimen ('{localCurrencyCode}'). Corrija la configuración de la regla.");
        }

        decimal percentage = rule.Percentage ?? 0m;
        decimal fixedAmount = rule.FixedAmount ?? 0m;
        ValidateRuleAmounts(rule, percentage, fixedAmount);

        decimal baseAmount = usesUsd ? amountUsd : amountLocal;
        decimal feeAmountUsd;
        decimal feeAmountLocal;

        if (usesUsd)
        {
            feeAmountUsd = Math.Round(
                baseAmount * percentage / 100m + fixedAmount,
                4,
                MidpointRounding.AwayFromZero);
            feeAmountLocal = rate > 0m
                ? Math.Round(feeAmountUsd * rate, 2, MidpointRounding.AwayFromZero)
                : 0m;
        }
        else
        {
            feeAmountLocal = Math.Round(
                baseAmount * percentage / 100m + fixedAmount,
                2,
                MidpointRounding.AwayFromZero);
            feeAmountUsd = rate > 0m
                ? Math.Round(feeAmountLocal / rate, 4, MidpointRounding.AwayFromZero)
                : 0m;
        }

        return new PaymentMethodFeeSnapshot(
            RuleId: rule.Id,
            FeeType: rule.FeeType,
            FeePercentage: rule.Percentage,
            FeeFixedAmount: rule.FixedAmount,
            FeeCurrencyCode: ruleCurrencyCode,
            FeeAmountUsd: feeAmountUsd,
            FeeAmountLocal: feeAmountLocal,
            FeeRate: rate,
            FeeConfigured: true,
            FeeRuleEffectiveFromUtc: rule.EffectiveFromUtc);
    }

    /// <summary>
    /// Moneda local del regimen: la secundaria en Dual (fallback primaria) o la primaria en Single.
    /// </summary>
    public static string ResolveLocalCurrencyCode(MonetaryProfileDto profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        return profile.Mode == MonetaryMode.Dual
            ? profile.SecondaryCurrencyCode ?? profile.PrimaryCurrencyCode
            : profile.PrimaryCurrencyCode;
    }

    /// <summary>
    /// Overload for the service-layer monetary snapshot (mode persisted as text).
    /// </summary>
    public static string ResolveLocalCurrencyCode(
        string monetaryMode,
        string primaryCurrencyCode,
        string? secondaryCurrencyCode)
        => string.Equals(monetaryMode, "Dual", StringComparison.Ordinal)
            ? secondaryCurrencyCode ?? primaryCurrencyCode
            : primaryCurrencyCode;

    public static void ApplyFeeSnapshot(this SalePayment payment, PaymentMethodFeeSnapshot fee)
    {
        ArgumentNullException.ThrowIfNull(payment);
        ArgumentNullException.ThrowIfNull(fee);

        payment.PaymentMethodFeeRuleId = fee.RuleId;
        payment.FeeType = fee.FeeType;
        payment.FeePercentage = fee.FeePercentage;
        payment.FeeFixedAmount = fee.FeeFixedAmount;
        payment.FeeCurrencyCode = fee.FeeCurrencyCode;
        payment.FeeAmountUsd = fee.FeeAmountUsd;
        payment.FeeAmountLocal = fee.FeeAmountLocal;
        payment.FeeRate = fee.FeeRate;
        payment.FeeConfigured = fee.FeeConfigured;
        payment.FeeRuleEffectiveFromUtc = fee.FeeRuleEffectiveFromUtc;
    }

    public static void ApplyFeeSnapshot(this ReceivablePayment payment, PaymentMethodFeeSnapshot fee)
    {
        ArgumentNullException.ThrowIfNull(payment);
        ArgumentNullException.ThrowIfNull(fee);

        payment.PaymentMethodFeeRuleId = fee.RuleId;
        payment.FeeType = fee.FeeType;
        payment.FeePercentage = fee.FeePercentage;
        payment.FeeFixedAmount = fee.FeeFixedAmount;
        payment.FeeCurrencyCode = fee.FeeCurrencyCode;
        payment.FeeAmountUsd = fee.FeeAmountUsd;
        payment.FeeAmountLocal = fee.FeeAmountLocal;
        payment.FeeRate = fee.FeeRate;
        payment.FeeConfigured = fee.FeeConfigured;
        payment.FeeRuleEffectiveFromUtc = fee.FeeRuleEffectiveFromUtc;
    }

    private static void ValidateRuleAmounts(
        PaymentMethodFeeRule rule,
        decimal percentage,
        decimal fixedAmount)
    {
        bool isValid = rule.FeeType switch
        {
            // El porcentaje debe quedar en (0, 100]; un valor mayor simularia una comision
            // imposible y distorsionaria la rentabilidad. El fijo nunca puede ser negativo.
            PaymentMethodFeeType.Percentage => percentage > 0m && percentage <= 100m,
            PaymentMethodFeeType.Fixed => fixedAmount > 0m,
            PaymentMethodFeeType.PercentagePlusFixed => percentage >= 0m
                && percentage <= 100m
                && fixedAmount >= 0m
                && (percentage > 0m || fixedAmount > 0m),
            _ => false
        };

        if (!isValid)
        {
            throw new InvalidOperationException(
                $"La regla de comisión (Id={rule.Id}) tiene una configuración inválida para el tipo '{rule.FeeType}': porcentaje={rule.Percentage}, monto fijo={rule.FixedAmount}. Corrija la configuración de la regla.");
        }
    }
}
