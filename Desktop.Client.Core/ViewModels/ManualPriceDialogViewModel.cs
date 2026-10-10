using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Helpers;

namespace Desktop.Client.ViewModels;

/// <summary>8.151 (W4, R8/design D7): resultado aceptado del dialogo de precio manual.</summary>
public sealed record ManualPriceDialogResult(decimal UnitPriceUsd, decimal UnitPriceLocal);

/// <summary>
/// 8.151 (W4, R8/design D7): captura de un precio manual (USD y/o Bs.S) para un producto normal.
/// Exige al menos un monto positivo, rechaza negativos/cero/invalidos con error inline y deriva la
/// moneda omitida con la tasa vigente (Bs.S con techo de 2 decimales; USD redondeado a 2).
/// </summary>
public partial class ManualPriceDialogViewModel : ObservableObject
{
    public const string InvalidAmountMessage = "Ingrese un monto válido mayor a 0.";
    public const string MissingAmountMessage = "Ingrese al menos un precio en dólares o bolívares.";
    public const string MissingRateMessage = "No hay una tasa de cambio válida para calcular la moneda faltante.";

    private readonly decimal _exchangeRate;

    [ObservableProperty]
    private string _unitPriceUsdText = string.Empty;

    [ObservableProperty]
    private string _unitPriceBsText = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public ManualPriceDialogResult? Result { get; private set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public ManualPriceDialogViewModel(decimal exchangeRate)
    {
        _exchangeRate = exchangeRate;
    }

    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(HasError));

    [RelayCommand]
    private void Accept()
    {
        ErrorMessage = string.Empty;
        Result = null;

        if (!TryParseAmount(UnitPriceUsdText, out var usd) || !TryParseAmount(UnitPriceBsText, out var local))
        {
            ErrorMessage = InvalidAmountMessage;
            return;
        }

        if (usd is null && local is null)
        {
            ErrorMessage = MissingAmountMessage;
            return;
        }

        if ((usd.HasValue && usd.Value <= 0) || (local.HasValue && local.Value <= 0))
        {
            ErrorMessage = InvalidAmountMessage;
            return;
        }

        if (usd.HasValue && local.HasValue)
        {
            Result = new ManualPriceDialogResult(usd.Value, local.Value);
            return;
        }

        if (_exchangeRate <= 0)
        {
            ErrorMessage = MissingRateMessage;
            return;
        }

        if (usd.HasValue)
        {
            Result = new ManualPriceDialogResult(usd.Value, PricingHelper.ToBsSCeiling(usd.Value, _exchangeRate));
            return;
        }

        var localValue = local!.Value;
        Result = new ManualPriceDialogResult(PricingHelper.ToUSD(localValue, _exchangeRate), localValue);
    }

    [RelayCommand]
    private void Cancel() => Result = null;

    /// <summary>
    /// Acepta vacio (omitido), separador decimal punto o coma y signo; devuelve null en field vacio.
    /// Un texto no numerico o con mas de un separador devuelve false.
    /// </summary>
    private static bool TryParseAmount(string? text, out decimal? amount)
    {
        amount = null;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return true;
        }

        var normalized = trimmed.Replace(',', '.');
        if (normalized.Count(c => c == '.') > 1)
        {
            return false;
        }

        if (decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed))
        {
            amount = parsed;
            return true;
        }

        return false;
    }
}
