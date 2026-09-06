using CommunityToolkit.Mvvm.Input;
using Core.DTOs;
using System;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class CustomerManagementViewModel
{
    [RelayCommand]
    public void NewCustomer()
    {
        SelectedCustomer = null;
        ResetForm();
    }

    private void ResetForm()
    {
        CedulaOrRif = string.Empty;
        Name = string.Empty;
        Phone = string.Empty;
        CreditLimitText = "0.00";
        IsActive = true;
    }

    [RelayCommand]
    public async Task SaveCustomerAsync()
    {
        if (!CanMutate)
        {
            _dialogService.ShowWarning("Acceso Denegado", "Los cajeros no tienen permisos para crear o modificar clientes.");
            return;
        }

        if (string.IsNullOrWhiteSpace(CedulaOrRif))
        {
            _dialogService.ShowWarning("Validación de Cliente", "La Cédula o RIF es obligatoria.");
            return;
        }

        if (!IsCedulaValid)
        {
            _dialogService.ShowWarning("Validación de Cliente", "La Cédula o RIF debe tener el formato oficial (ej. V-12345678 con 7 u 8 dígitos).");
            return;
        }

        if (string.IsNullOrWhiteSpace(Name))
        {
            _dialogService.ShowWarning("Validación de Cliente", "El Nombre o Razón Social es obligatorio.");
            return;
        }

        if (Name.Trim().Length > 50)
        {
            _dialogService.ShowWarning("Validación de Cliente", "El Nombre o Razón Social no puede exceder los 50 caracteres.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(Phone) && !IsPhoneValid)
        {
            _dialogService.ShowWarning("Validación de Cliente", "El teléfono debe tener 11 dígitos y una operadora válida (ej. 0412-1234567).");
            return;
        }

        decimal.TryParse(CreditLimitText, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out decimal creditLimit);

        IsLoading = true;
        try
        {
            if (IsEditing && SelectedCustomer != null)
            {
                var updated = await _salesService.UpdateCustomerAsync(SelectedCustomer.Id, new UpdateCustomerDto
                {
                    CedulaOrRif = CedulaOrRif.Trim(),
                    Name = Name.Trim(),
                    Phone = Phone.Trim(),
                    CreditLimitUSD = creditLimit,
                    IsActive = IsActive
                });

                _dialogService.ShowSuccessDialog($"Cliente '{updated.Name}' actualizado correctamente.");
            }
            else
            {
                var created = await _salesService.CreateCustomerAsync(new CreateCustomerDto
                {
                    CedulaOrRif = CedulaOrRif.Trim(),
                    Name = Name.Trim(),
                    Phone = Phone.Trim(),
                    CreditLimitUSD = creditLimit
                });

                _dialogService.ShowSuccessDialog($"Cliente '{created.Name}' registrado con éxito.");
            }

            await LoadCustomersAsync();
            NewCustomer();
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Error al Guardar Cliente", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task ToggleActiveCustomerAsync()
    {
        if (!CanMutate)
        {
            _dialogService.ShowWarning("Acceso Denegado", "Los cajeros no tienen permisos para modificar clientes.");
            return;
        }

        if (SelectedCustomer == null) return;

        if (IsDefaultCustomer)
        {
            _dialogService.ShowWarning("Operación No Permitida", "No se permite desactivar el cliente Consumidor Final predeterminado.");
            return;
        }

        bool newStatus = !IsActive;
        string actionName = newStatus ? "activar" : "desactivar";

        bool confirm = _dialogService.ShowConfirm(
            $"Confirmar {actionName.ToUpper()}",
            $"¿Desea {actionName} al cliente '{Name}' ({CedulaOrRif})?");

        if (!confirm) return;

        IsLoading = true;
        try
        {
            var updated = await _salesService.UpdateCustomerAsync(SelectedCustomer.Id, new UpdateCustomerDto
            {
                CedulaOrRif = CedulaOrRif.Trim(),
                Name = Name.Trim(),
                Phone = Phone.Trim(),
                CreditLimitUSD = decimal.TryParse(CreditLimitText, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var limit) ? limit : 0m,
                IsActive = newStatus
            });

            IsActive = updated.IsActive;
            _dialogService.ShowSuccessDialog($"Cliente '{updated.Name}' {(newStatus ? "activado" : "desactivado")} correctamente.");
            await LoadCustomersAsync();
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Error al Modificar Estado", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task DeleteCustomerAsync()
    {
        if (!CanMutate)
        {
            _dialogService.ShowWarning("Acceso Denegado", "Los cajeros no tienen permisos para eliminar clientes.");
            return;
        }

        if (SelectedCustomer == null) return;

        if (IsDefaultCustomer)
        {
            _dialogService.ShowWarning("Operación No Permitida", "No se permite eliminar el cliente Consumidor Final predeterminado.");
            return;
        }

        bool confirm = _dialogService.ShowConfirm(
            "Eliminar Cliente",
            $"¿Desea eliminar definitivamente al cliente '{Name}' ({CedulaOrRif})?\n\nEsta acción eliminará el cliente si no posee ventas asociadas.");

        if (!confirm) return;

        IsLoading = true;
        try
        {
            await _salesService.DeleteCustomerAsync(SelectedCustomer.Id);
            _dialogService.ShowSuccessDialog($"Cliente '{Name}' eliminado correctamente.");
            await LoadCustomersAsync();
            NewCustomer();
        }
        catch (Exception ex)
        {
            _dialogService.ShowWarning("Advertencia de Eliminación", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }
}
