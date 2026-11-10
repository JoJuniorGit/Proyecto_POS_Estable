using Core.Helpers;
using Desktop.Client.Services;
using System;
using System.Linq;

namespace Desktop.Client.ViewModels;

/// <summary>
/// F4-WU1 (Paso 14): validación local del formulario de comisiones. Reproduce las reglas del
/// servicio F3 (porcentaje 0-100, fijo positivo, vigencia ordenada y moneda obligatoria) para que
/// un valor inválido nunca llegue a la API.
/// </summary>
public partial class SettingsViewModel
{
    private bool TryBuildFeeValues(
        out PaymentMethodFeeTypeClient feeType,
        out decimal? percentage,
        out decimal? fixedAmount,
        out DateTime effectiveFromUtc,
        out DateTime? effectiveToUtc,
        out string error)
    {
        percentage = null;
        fixedAmount = null;
        effectiveFromUtc = DateTime.UtcNow;
        effectiveToUtc = null;
        error = string.Empty;
        feeType = SelectedFeeTypeOption?.Value ?? PaymentMethodFeeTypeClient.Percentage;

        if (string.IsNullOrWhiteSpace(FeeCurrencyCode))
        {
            error = "La moneda de la comisión es obligatoria.";
            return false;
        }

        if (!FeeEffectiveFromDate.HasValue)
        {
            error = "La fecha de inicio de vigencia es obligatoria.";
            return false;
        }

        effectiveFromUtc = DateTime.SpecifyKind(FeeEffectiveFromDate.Value.Date, DateTimeKind.Utc);
        if (FeeEffectiveToDate.HasValue)
        {
            effectiveToUtc = DateTime.SpecifyKind(FeeEffectiveToDate.Value.Date, DateTimeKind.Utc);
            if (effectiveFromUtc >= effectiveToUtc.Value)
            {
                error = "La fecha de inicio de vigencia debe ser anterior a la fecha de fin de vigencia.";
                return false;
            }
        }

        switch (feeType)
        {
            case PaymentMethodFeeTypeClient.Percentage:
                if (!TryParsePercentage(out var parsedPercentage, out error))
                {
                    return false;
                }

                percentage = parsedPercentage;
                return true;

            case PaymentMethodFeeTypeClient.Fixed:
                if (!FlexibleDecimalParser.TryParse(FeeFixedAmountText, out var parsedFixed) || parsedFixed <= 0m)
                {
                    error = "El monto fijo de comisión debe ser mayor que cero.";
                    return false;
                }

                fixedAmount = parsedFixed;
                return true;

            case PaymentMethodFeeTypeClient.PercentagePlusFixed:
                if (!TryParsePercentage(out var combinedPercentage, out error))
                {
                    return false;
                }

                decimal combinedFixed = 0m;
                if (!string.IsNullOrWhiteSpace(FeeFixedAmountText)
                    && (!FlexibleDecimalParser.TryParse(FeeFixedAmountText, out combinedFixed) || combinedFixed < 0m))
                {
                    error = "El monto fijo de comisión no puede ser negativo.";
                    return false;
                }

                percentage = combinedPercentage;
                fixedAmount = combinedFixed;
                return true;

            default:
                error = "El tipo de comisión seleccionado no es válido.";
                return false;
        }
    }

    private bool TryParsePercentage(out decimal percentage, out string error)
    {
        percentage = 0m;
        if (!FlexibleDecimalParser.TryParse(FeePercentageText, out percentage)
            || percentage <= 0m
            || percentage > 100m)
        {
            error = "El porcentaje de comisión debe ser mayor que cero y menor o igual a 100.";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private void ReplaceFeeRule(PaymentMethodFeeRuleClientDto saved)
    {
        for (var index = 0; index < PaymentMethodFeeRules.Count; index++)
        {
            if (PaymentMethodFeeRules[index].Id == saved.Id)
            {
                PaymentMethodFeeRules[index] = saved;
                return;
            }
        }

        PaymentMethodFeeRules.Add(saved);
    }

    private void SyncFeeEligiblePaymentMethods()
    {
        FeeEligiblePaymentMethods.Clear();
        foreach (var method in PaymentMethods.Where(method => !method.IsCash && !method.IsDeleted))
        {
            FeeEligiblePaymentMethods.Add(method);
        }

        if (SelectedFeePaymentMethod != null
            && !FeeEligiblePaymentMethods.Any(method => method.Id == SelectedFeePaymentMethod.Id))
        {
            SelectedFeePaymentMethod = FeeEligiblePaymentMethods.FirstOrDefault();
        }
    }

    private void RefreshFeeCurrencyOptions()
    {
        var localCode = CurrencyDisplay.LocalCurrencyCode;
        var codes = new[] { "USD", localCode }
            .Where(code => !string.IsNullOrWhiteSpace(code))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        FeeCurrencyOptions.Clear();
        foreach (var code in codes)
        {
            FeeCurrencyOptions.Add(code);
        }

        if (!FeeCurrencyOptions.Contains(FeeCurrencyCode, StringComparer.Ordinal))
        {
            FeeCurrencyCode = FeeCurrencyOptions.FirstOrDefault() ?? "USD";
        }
    }

    private static string FormatFeeDecimal(decimal? value)
    {
        if (!value.HasValue)
        {
            return string.Empty;
        }

        return CurrencyDisplay.Number(value.Value, 4).TrimEnd('0').TrimEnd('.', ',');
    }
}
