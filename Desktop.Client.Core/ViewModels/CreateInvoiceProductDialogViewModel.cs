using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;

namespace Desktop.Client.ViewModels;

/// <summary>
/// 8.146-T5 (S4): captura obligatoria del código de barras universal (EAN/UPC) al crear un
/// producto desde una línea de factura. El código del proveedor (SupplierCode/Barcode de la
/// factura) nunca se hereda como SKU del producto físico.
/// </summary>
public partial class CreateInvoiceProductDialogViewModel : ObservableValidator, IDisposable
{
    public Action<bool>? RequestClose;

    [ObservableProperty]
    [Required(ErrorMessage = "El código de barras es obligatorio.")]
    [RegularExpression(@"^\d{8,14}$", ErrorMessage = "El código de barras debe tener entre 8 y 14 dígitos numéricos.")]
    private string _barcode = string.Empty;

    [ObservableProperty]
    [Required(ErrorMessage = "El nombre del producto es obligatorio.")]
    [MaxLength(100, ErrorMessage = "El nombre del producto no puede superar los 100 caracteres.")]
    private string _name = string.Empty;

    /// <summary>Contexto de solo lectura: nombre original de la línea de factura.</summary>
    public string ProductName { get; }

    /// <summary>Contexto de solo lectura: costo normalizado en USD y, para facturas Bs.S, costo de la factura.</summary>
    public string CostInfo { get; }

    [ObservableProperty]
    private bool _isSaving;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public CreateInvoiceProductDialogViewModel(SupplierInvoiceLineViewModel line, bool isBsSCurrency)
    {
        ArgumentNullException.ThrowIfNull(line);

        ProductName = line.Name ?? string.Empty;
        Name = ProductName.Trim();
        CostInfo = isBsSCurrency
            ? $"Costo normalizado: ${line.UnitCostUSD:N2} · Costo factura: Bs.S {line.UnitCostDocument:N2} · Cantidad: {line.Quantity:N3}"
            : $"Costo normalizado: ${line.UnitCostUSD:N2} · Cantidad: {line.Quantity:N3}";
    }

    private bool CanSave => !IsSaving;

    partial void OnIsSavingChanged(bool value) => SaveCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (IsSaving)
        {
            return;
        }

        ErrorMessage = string.Empty;
        Barcode = Barcode.Trim();
        Name = Name.Trim();
        ValidateAllProperties();
        if (HasErrors)
        {
            ErrorMessage = GetErrors(null).FirstOrDefault()?.ErrorMessage ?? "Revise los datos del producto.";
            return;
        }

        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);

    public void Dispose() => RequestClose = null;
}
