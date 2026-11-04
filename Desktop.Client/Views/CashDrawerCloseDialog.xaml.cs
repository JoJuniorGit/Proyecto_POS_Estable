using System;
using System.Globalization;
using System.Windows;
using Desktop.Client.Services;

namespace Desktop.Client.Views;

/// <summary>
/// T4 (Paso 13 / 8.188, S2): arqueo de cierre de la sesión de caja. El conteo Bs.S es obligatorio
/// y el USD es opcional; sin el segundo, el cierre conserva el comportamiento previo.
/// </summary>
public partial class CashDrawerCloseDialog : Window
{
    public CashDrawerCloseDialog(decimal exchangeRate, decimal currentBalanceBsS, decimal? currentBalanceUsd)
    {
        InitializeComponent();

        RateText.Text = $"Tasa vigente: {CurrencyDisplay.LocalCurrencyCode} {CurrencyDisplay.Number(exchangeRate, 2)} / {CurrencyDisplay.PrimaryCurrencyCode}";
        TheoreticalBsSText.Text = $"Efectivo esperado {CurrencyDisplay.LocalCurrencyCode}: {CurrencyDisplay.FormatSecondary(currentBalanceBsS)}";
        TheoreticalUsdText.Text = currentBalanceUsd.HasValue
            ? $"Efectivo esperado {CurrencyDisplay.PrimaryCurrencyCode}: {CurrencyDisplay.FormatPrimary(currentBalanceUsd.Value)}"
            : $"Efectivo esperado {CurrencyDisplay.PrimaryCurrencyCode}: no disponible";

        LocalAmountBox.Text = currentBalanceBsS.ToString("N2", CultureInfo.InvariantCulture);
        Loaded += (_, _) => LocalAmountBox.Focus();
    }

    public decimal ActualClosingBalanceLocal { get; private set; }

    public decimal? ActualClosingBalanceUsd { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseAmount(LocalAmountBox.Text, out decimal local) || local < 0m)
        {
            ShowError($"Ingrese un conteo válido de efectivo {CurrencyDisplay.LocalCurrencyCode} (mayor o igual a cero).");
            return;
        }

        decimal? usd = null;
        if (!string.IsNullOrWhiteSpace(UsdAmountBox.Text))
        {
            if (!TryParseAmount(UsdAmountBox.Text, out decimal parsedUsd) || parsedUsd < 0m)
            {
                ShowError($"Ingrese un conteo válido de efectivo {CurrencyDisplay.PrimaryCurrencyCode} (mayor o igual a cero) o déjelo vacío.");
                return;
            }

            usd = parsedUsd;
        }

        ActualClosingBalanceLocal = local;
        ActualClosingBalanceUsd = usd;
        DialogResult = true;
    }

    private static bool TryParseAmount(string? text, out decimal value)
    {
        value = 0m;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string clean = text.Trim();
        if (clean.Contains(',') && !clean.Contains('.'))
        {
            clean = clean.Replace(',', '.');
        }

        return decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out value)
            || decimal.TryParse(clean, NumberStyles.Any, CultureInfo.CurrentCulture, out value);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
