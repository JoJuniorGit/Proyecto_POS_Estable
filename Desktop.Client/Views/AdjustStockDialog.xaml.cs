using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Text.RegularExpressions;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class AdjustStockDialog : Window
{
    public decimal QuantityChange { get; private set; }
    public string Reason { get; private set; } = string.Empty;

    /// <summary>8.177-T3 (S1/S4): clasificación estandarizada obligatoria del ajuste manual.</summary>
    public StockMovementReason? ReasonCode { get; private set; }

    /// <summary>8.177-T3 (S1/S4): observación obligatoria del ajuste manual.</summary>
    public string? Observation { get; private set; }

    public AdjustStockDialog(ProductDto product)
    {
        InitializeComponent();
        CurrentStockText.Text = $"Disponible actual: {product.StockQuantity.ToString("#,##0.###", System.Globalization.CultureInfo.CurrentCulture)} {product.UnitOfMeasure}";
        ReasonCodeCombo.ItemsSource = Enum.GetValues<StockMovementReason>()
            .Select(reason => new StockMovementReasonOption(reason, StockMovementLabels.Reason(reason)))
            .ToList();
        QuantityInput.Focus();
    }

    private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
    {
        // Allow digits and decimal separator
        Regex regex = new Regex(@"[^0-9.,]+");
        e.Handled = regex.IsMatch(e.Text);
    }

    /// <summary>
    /// 8.177-T3 (S1/S4): validación pura del ajuste manual (testeable sin instanciar la ventana).
    /// Devuelve el mensaje y el campo a enfocar cuando el ajuste no puede confirmarse.
    /// </summary>
    public static (bool IsValid, string Message, string FocusField) ValidateAdjustment(
        string? quantityText,
        string? reasonText,
        StockMovementReason? reasonCode,
        string? observation)
    {
        var rawText = (quantityText ?? string.Empty).Replace(',', '.');
        if (!decimal.TryParse(rawText, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal absoluteQty) || absoluteQty <= 0)
        {
            return (false, "Por favor ingrese una cantidad positiva mayor a 0.", "quantity");
        }

        if (string.IsNullOrWhiteSpace(reasonText) || reasonText.Trim().Length < 3)
        {
            return (false, "Debe ingresar un motivo o descripción explícita (mínimo 3 caracteres) para justificar el ajuste de inventario.", "reason");
        }

        if (reasonCode == null)
        {
            return (false, "Debe seleccionar un motivo estandarizado (obligatorio para la auditoría del movimiento).", "reasonCode");
        }

        if (string.IsNullOrWhiteSpace(observation))
        {
            return (false, "La observación es obligatoria para la auditoría del ajuste manual.", "observation");
        }

        if (observation.Trim().Length > 500)
        {
            return (false, "La observación no puede exceder los 500 caracteres.", "observation");
        }

        return (true, string.Empty, string.Empty);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ValidationMessageText.Visibility = Visibility.Collapsed;

        var selectedReason = ReasonCodeCombo.SelectedItem as StockMovementReasonOption;
        var validation = ValidateAdjustment(QuantityInput.Text, ReasonInput.Text, selectedReason?.Value, ObservationInput.Text);
        if (!validation.IsValid)
        {
            ShowValidationMessage(validation.Message);
            FocusInvalidField(validation.FocusField);
            return;
        }

        // Calculate exact quantity change vector based on explicit layout selection rather than +- typing.
        var rawText = QuantityInput.Text.Replace(',', '.');
        _ = decimal.TryParse(rawText, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal absoluteQty);
        bool isDecrease = RadioDecrease.IsChecked == true;
        QuantityChange = isDecrease ? -absoluteQty : absoluteQty;
        Reason = ReasonInput.Text.Trim();
        ReasonCode = selectedReason!.Value;
        Observation = ObservationInput.Text.Trim();

        DialogResult = true;
    }

    private void FocusInvalidField(string field)
    {
        switch (field)
        {
            case "quantity":
                QuantityInput.Focus();
                break;
            case "reason":
                ReasonInput.Focus();
                break;
            case "reasonCode":
                ReasonCodeCombo.Focus();
                break;
            case "observation":
                ObservationInput.Focus();
                break;
        }
    }

    private void ShowValidationMessage(string message)
    {
        ValidationMessageText.Text = message;
        ValidationMessageText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
