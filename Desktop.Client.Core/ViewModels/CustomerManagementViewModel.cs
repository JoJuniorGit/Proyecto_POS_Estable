using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class CustomerManagementViewModel : ObservableObject
{
    private readonly ISalesService _salesService;
    private readonly UserSession _userSession;
    private readonly IDialogService _dialogService;
    private CancellationTokenSource? _searchCts;

    private static readonly HashSet<string> ValidRifPrefixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "V", "E", "J", "G", "P"
    };

    private static readonly HashSet<string> ValidPhonePrefixes = new()
    {
        "0412", "0414", "0424", "0416", "0426", "0212", "0241", "0242", "0243", "0244", "0245", "0251", "0276", "0261"
    };

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing))]
    [NotifyPropertyChangedFor(nameof(IsDefaultCustomer))]
    private CustomerDto? _selectedCustomer;

    public bool IsEditing => SelectedCustomer != null;
    public bool IsDefaultCustomer => SelectedCustomer?.IsDefault == true || SelectedCustomer?.CedulaOrRif.Equals("V-00000000", StringComparison.OrdinalIgnoreCase) == true;

    // RBAC: Cashiers cannot create, edit, deactivate, or delete customers
    public bool CanMutate => _userSession.CurrentUser != null && _userSession.CurrentUser.Role != UserRole.Cashier;

    public ObservableCollection<CustomerDto> Customers { get; } = new();

    // Form fields
    [ObservableProperty] private string _cedulaOrRif = string.Empty;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _phone = string.Empty;
    [ObservableProperty] private string _creditLimitText = "0.00";
    [ObservableProperty] private bool _isActive = true;

    // Dynamic Validation Indicators & Length Tracking
    [ObservableProperty] private bool _isCedulaValid;
    [ObservableProperty] private bool _isPhoneValid;

    public int NameLength => Name?.Length ?? 0;
    public bool IsNameNearLimit => NameLength >= 42;

    private string _searchQuery = string.Empty;
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            if (SetProperty(ref _searchQuery, value))
            {
                var newCts = new CancellationTokenSource();
                var oldCts = Interlocked.Exchange(ref _searchCts, newCts);
                try
                {
                    oldCts?.Cancel();
                    oldCts?.Dispose();
                }
                catch (ObjectDisposedException) { }

                StartDebouncedSearch(value, newCts.Token);
            }
        }
    }

    public CustomerManagementViewModel(
        ISalesService salesService,
        UserSession userSession,
        IDialogService dialogService)
    {
        _salesService = salesService;
        _userSession = userSession;
        _dialogService = dialogService;
    }

    public async Task InitializeAsync()
    {
        await LoadCustomersAsync();
    }

    partial void OnSelectedCustomerChanged(CustomerDto? value)
    {
        if (value != null)
        {
            CedulaOrRif = value.CedulaOrRif;
            Name = value.Name;
            Phone = value.Phone;
            CreditLimitText = value.CreditLimitUSD.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            IsActive = value.IsActive;
        }
        else
        {
            ResetForm();
        }
    }

    // ── Controlled Input: Cédula / RIF (V or E + 7-8 digits) ──
    partial void OnCedulaOrRifChanged(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            IsCedulaValid = false;
            return;
        }

        string formatted = FormatCedulaRifInput(value);
        if (formatted != value)
        {
            CedulaOrRif = formatted;
            return;
        }

        IsCedulaValid = Regex.IsMatch(formatted, @"^[VJEGPvjegp]-\d{7,8}$", RegexOptions.IgnoreCase);
    }

    private static string FormatCedulaRifInput(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        input = input.Trim().ToUpperInvariant();

        if (char.IsDigit(input[0]))
        {
            input = "V-" + input;
        }

        string firstChar = input.Substring(0, 1);
        if (!ValidRifPrefixes.Contains(firstChar))
        {
            return string.Empty;
        }

        string digits = Regex.Replace(input.Substring(1), @"\D", "");
        if (digits.Length > 8)
        {
            digits = digits.Substring(0, 8);
        }

        return $"{firstChar}-{digits}";
    }

    // ── Controlled Input: Phone (11 digits with operator hyphen) ──
    partial void OnPhoneChanged(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            IsPhoneValid = false;
            return;
        }

        string digits = Regex.Replace(value, @"\D", "");
        if (digits.Length > 11)
        {
            digits = digits.Substring(0, 11);
        }

        if (digits.Length >= 4)
        {
            string prefix4 = digits.Substring(0, 4);
            if (!ValidPhonePrefixes.Contains(prefix4))
            {
                digits = digits.Substring(0, 3);
            }
        }

        string formatted = digits;
        if (digits.Length > 4)
        {
            formatted = $"{digits.Substring(0, 4)}-{digits.Substring(4)}";
        }

        if (formatted != value)
        {
            Phone = formatted;
            return;
        }

        IsPhoneValid = digits.Length == 11 && ValidPhonePrefixes.Contains(digits.Substring(0, 4));
    }

    // ── Controlled Input: Credit Limit (ATM Shift Effect 0.0x) ──
    partial void OnCreditLimitTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "0" || value == "0.00")
        {
            if (value != "0.00") CreditLimitText = "0.00";
            return;
        }

        string digits = Regex.Replace(value, @"\D", "").TrimStart('0');
        if (string.IsNullOrEmpty(digits))
        {
            if (value != "0.00") CreditLimitText = "0.00";
            return;
        }

        if (long.TryParse(digits, out long cents))
        {
            decimal dollars = cents / 100.00m;
            string formatted = dollars.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
            if (value != formatted)
            {
                CreditLimitText = formatted;
            }
        }
    }

    partial void OnNameChanged(string value)
    {
        OnPropertyChanged(nameof(NameLength));
        OnPropertyChanged(nameof(IsNameNearLimit));
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageSummary))]
    [NotifyPropertyChangedFor(nameof(CanGoToPreviousPage))]
    [NotifyPropertyChangedFor(nameof(CanGoToNextPage))]
    private int _currentPage = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageSummary))]
    [NotifyPropertyChangedFor(nameof(CanGoToPreviousPage))]
    [NotifyPropertyChangedFor(nameof(CanGoToNextPage))]
    private int _totalPages = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageSummary))]
    private int _totalCount = 0;

    [ObservableProperty] private int _pageSize = 20;

    public string PageSummary => $"Página {CurrentPage} de {Math.Max(1, TotalPages)} (Total: {TotalCount})";
    public bool CanGoToPreviousPage => CurrentPage > 1;
    public bool CanGoToNextPage => CurrentPage < TotalPages;

    private async void StartDebouncedSearch(string query, CancellationToken token)
    {
        try
        {
            await Task.Delay(300, token);
            if (!token.IsCancellationRequested)
            {
                CurrentPage = 1;
                await LoadCustomersAsync(query);
            }
        }
        catch (OperationCanceledException) { }
    }

    [RelayCommand]
    public async Task LoadCustomersAsync(string? query = null)
    {
        IsLoading = true;
        StatusMessage = "Cargando clientes...";
        try
        {
            var (items, total) = await _salesService.GetCustomersAsync(query ?? SearchQuery, CurrentPage, PageSize, recentOnly: false);
            TotalCount = total;
            TotalPages = (int)Math.Ceiling((double)total / PageSize);
            if (TotalPages < 1) TotalPages = 1;

            Customers.Clear();
            foreach (var c in items)
            {
                Customers.Add(c);
            }
            StatusMessage = $"Mostrando {Customers.Count} de {TotalCount} clientes";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error al cargar clientes: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task PreviousPageAsync()
    {
        if (CanGoToPreviousPage)
        {
            CurrentPage--;
            await LoadCustomersAsync();
        }
    }

    [RelayCommand]
    public async Task NextPageAsync()
    {
        if (CanGoToNextPage)
        {
            CurrentPage++;
            await LoadCustomersAsync();
        }
    }


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
