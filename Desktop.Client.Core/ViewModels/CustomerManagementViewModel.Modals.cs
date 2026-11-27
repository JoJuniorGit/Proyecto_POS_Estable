using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Common;
using Core.DTOs;

namespace Desktop.Client.ViewModels;

/// <summary>
/// Estado del modal de cliente. La edición hidrata campos independientes; Cancelar no muta el
/// DTO de la grilla. El aviso de éxito se eleva al shell mediante <see cref="ToastRequested"/>.
/// </summary>
public partial class CustomerManagementViewModel
{
    [ObservableProperty]
    private bool _isCustomerModalOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomerModalError))]
    private string? _customerModalError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomerSaveEnabled))]
    private bool _isSavingCustomer;

    public event Action<string>? ToastRequested;

    public bool HasCustomerModalError => !string.IsNullOrWhiteSpace(CustomerModalError);

    public bool IsCustomerSaveEnabled => CanEditCustomerContact && !IsSavingCustomer;

    public string CustomerModalTitle => IsEditing ? "Editar Cliente" : "Nuevo Cliente";

    public string CustomerModalSubtitle => IsEditing
        ? "Actualiza los datos fiscales y de contacto"
        : "Registra los datos fiscales y de contacto";

    [RelayCommand]
    private void OpenNewCustomer()
    {
        NewCustomer();
        CustomerModalError = null;
        IsSavingCustomer = false;
        IsCustomerModalOpen = true;
    }

    [RelayCommand]
    private void OpenEditCustomer(CustomerDto? customer)
    {
        if (customer is null) return;

        SelectedCustomer = customer;
        HydrateCustomerForm(customer);
        CustomerModalError = null;
        IsSavingCustomer = false;
        IsCustomerModalOpen = true;
    }

    [RelayCommand]
    private void CloseCustomerModal()
    {
        IsCustomerModalOpen = false;
        CustomerModalError = null;
        NewCustomer();
    }

    [RelayCommand]
    private void ClearCustomerSearch() => SearchQuery = string.Empty;

    [RelayCommand]
    private void ClearCustomerFilters()
    {
        SearchQuery = string.Empty;
        SelectedCustomerStatusFilter = CustomerStatusFilters[0];
        CurrentPage = 1;
        LoadCustomersAsync().SafeFireAndForget("CustomerManagement.ClearFilters");
    }

    private void HydrateCustomerForm(CustomerDto customer)
    {
        CedulaOrRif = customer.CedulaOrRif;
        Name = customer.Name;
        Phone = customer.Phone;
        CreditLimitText = customer.CreditLimitUSD.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        IsActive = customer.IsActive;
    }
}
