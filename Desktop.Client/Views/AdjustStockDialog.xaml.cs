using System.Windows;
using System.Windows.Input;
using System.Text.RegularExpressions;
using Core.DTOs;

namespace Desktop.Client.Views;

public partial class AdjustStockDialog : Window
{
    public int QuantityChange { get; private set; }
    public string Reason { get; private set; } = string.Empty;

    public AdjustStockDialog(ProductDto product)
    {
        InitializeComponent();
        CurrentStockText.Text = $"Disponible actual: {product.StockQuantity} {product.UnitOfMeasure}";
        QuantityInput.Focus();
    }

    private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
    {
        // Require absolute positive number visually
        Regex regex = new Regex("[^0-9]+");
        e.Handled = regex.IsMatch(e.Text);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(QuantityInput.Text, out int absoluteQty) && absoluteQty > 0)
        {
            if (string.IsNullOrWhiteSpace(ReasonInput.Text) || ReasonInput.Text.Trim().Length < 3)
            {
                MessageBox.Show("Debe ingresar un motivo o descripción explícita (mínimo 3 caracteres) para justificar el ajuste de inventario.", "Error de Validación", MessageBoxButton.OK, MessageBoxImage.Warning);
                ReasonInput.Focus();
                return;
            }

            // Calculate exact quantity change vector based on explicit layout selection rather than +- typing.
            bool isDecrease = RadioDecrease.IsChecked == true;
            QuantityChange = isDecrease ? -absoluteQty : absoluteQty;
            Reason = ReasonInput.Text.Trim();

            DialogResult = true;
        }
        else
        {
            MessageBox.Show("Por favor ingrese una cantidad entera positiva mayor a 0.", "Error de Validación", MessageBoxButton.OK, MessageBoxImage.Warning);
            QuantityInput.Focus();
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
