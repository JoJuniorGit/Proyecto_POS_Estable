using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Desktop.Client.Services;

namespace Desktop.Client.Views;

public partial class PaymentMethodCreationDialog : Window
{
    public PaymentMethodCreationDialog()
    {
        InitializeComponent();
        FeeTypeComboBox.SelectedIndex = 0;
        UpdateCommissionFields();
        Loaded += (_, _) => NameTextBox.Focus();
    }

    public PaymentMethodDto? Result { get; private set; }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var name = NameTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowError("Ingresá el nombre del método de pago.");
            return;
        }

        var isCash = IsCashCheckBox.IsChecked == true;
        var feeType = FeeTypeComboBox.SelectedIndex;
        var hasPercentage = feeType is 0 or 2;
        var hasFixed = feeType is 1 or 2;

        var percentage = TryParseDecimal(PercentageTextBox.Text, out var parsedPercentage)
            ? parsedPercentage
            : 0m;
        var fixedAmount = TryParseDecimal(FixedAmountTextBox.Text, out var parsedFixed)
            ? parsedFixed
            : 0m;

        if (hasPercentage && (percentage <= 0m || percentage > 100m))
        {
            ShowError("El porcentaje de comisión debe ser mayor que cero y menor o igual a 100.");
            return;
        }

        if (feeType == 1 && fixedAmount <= 0m)
        {
            ShowError("El monto fijo por transacción debe ser mayor que cero.");
            return;
        }

        if (feeType == 2 && fixedAmount < 0m)
        {
            ShowError("El monto fijo por transacción no puede ser negativo.");
            return;
        }

        var hasPositiveAmount = (hasPercentage && percentage > 0m)
            || (hasFixed && fixedAmount > 0m);

        if (isCash && hasPositiveAmount)
        {
            ShowError("Un método de efectivo no puede tener comisión mayor que cero.");
            return;
        }

        var hasPositiveCommission = !isCash && hasPositiveAmount;

        var currencyCode = CurrencyTextBox.Text.Trim();
        if (hasPositiveCommission && string.IsNullOrWhiteSpace(currencyCode))
        {
            ShowError("La moneda de la comisión es obligatoria.");
            return;
        }

        Result = new PaymentMethodDto
        {
            Name = name,
            IsActive = true,
            IsCash = isCash,
            RequiresReference = RequiresReferenceCheckBox.IsChecked == true,
            Commission = hasPositiveCommission
                ? new PaymentMethodCommissionClientDto
                {
                    FeeType = feeType,
                    Percentage = hasPercentage ? percentage : null,
                    FixedAmount = hasFixed ? fixedAmount : null,
                    CurrencyCode = currencyCode,
                    IsActive = true
                }
                : null
        };

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Cash_Changed(object sender, RoutedEventArgs e)
    {
        if (IsCashCheckBox.IsChecked == true)
        {
            PercentageTextBox.Text = string.Empty;
            FixedAmountTextBox.Text = string.Empty;
        }

        UpdateCommissionFields();
    }

    private void FeeType_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateCommissionFields();

    private void UpdateCommissionFields()
    {
        if (FeeTypeComboBox == null
            || PercentageTextBox == null
            || FixedAmountTextBox == null
            || CurrencyTextBox == null)
        {
            return;
        }

        var isCash = IsCashCheckBox.IsChecked == true;
        var feeType = FeeTypeComboBox.SelectedIndex;
        PercentageTextBox.IsEnabled = !isCash && feeType != 1;
        FixedAmountTextBox.IsEnabled = !isCash && feeType != 0;
        CurrencyTextBox.IsEnabled = !isCash;
    }

    private static bool TryParseDecimal(string raw, out decimal value)
    {
        value = 0m;
        var text = (raw ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return false;
        }

        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.CurrentCulture, out value)
            || decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
