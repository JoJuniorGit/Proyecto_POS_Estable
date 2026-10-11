using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;

namespace Desktop.Client.ViewModels;

/// <summary>
/// 8.161: alta de proveedor desde la vista de facturas (RIF/NIT opcional + nombre comercial
/// obligatorio). Solo captura y valida; el registro lo ejecuta el ViewModel padre al cerrar
/// con resultado true.
/// </summary>
public partial class RegisterSupplierDialogViewModel : ObservableValidator, IDisposable
{
    public Action<bool>? RequestClose;

    [ObservableProperty]
    private string _rifOrNit = string.Empty;

    [ObservableProperty]
    [Required(ErrorMessage = "El nombre comercial es obligatorio.")]
    private string _commercialName = string.Empty;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    public RegisterSupplierDialogViewModel(string? rifOrNit, string? commercialName)
    {
        RifOrNit = rifOrNit?.Trim() ?? string.Empty;
        CommercialName = commercialName?.Trim() ?? string.Empty;
    }

    [RelayCommand]
    private void Save()
    {
        ErrorMessage = string.Empty;
        RifOrNit = RifOrNit.Trim();
        CommercialName = CommercialName.Trim();
        ValidateAllProperties();
        if (HasErrors)
        {
            ErrorMessage = GetErrors(null).FirstOrDefault()?.ErrorMessage ?? "Revise los datos del proveedor.";
            return;
        }

        RequestClose?.Invoke(true);
    }

    [RelayCommand]
    private void Cancel() => RequestClose?.Invoke(false);

    public void Dispose() => RequestClose = null;
}
