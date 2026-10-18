using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Core.DTOs;
using Core.Entities;
using Desktop.Client.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Core.Common;

namespace Desktop.Client.ViewModels;

public partial class CustomerManagementViewModel : ObservableObject, IDisposable
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
    [NotifyPropertyChangedFor(nameof(CanEditCreditProfile))]
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

    // ── Perfil crediticio (Paso 11, S1/S4) ──
    [ObservableProperty] private bool _isCreditEnabled;
    [ObservableProperty] private string _paymentTermDaysText = "30";
    [ObservableProperty] private string _creditGraceDaysText = "0";
    [ObservableProperty] private string _creditNotes = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditCreditProfile))]
    [NotifyPropertyChangedFor(nameof(CreditStatusLabel))]
    [NotifyPropertyChangedFor(nameof(IsManuallyBlocked))]
    private Core.Entities.CreditStatus _creditStatus = Core.Entities.CreditStatus.Active;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCreditProfile))]
    [NotifyPropertyChangedFor(nameof(CanEditCreditProfile))]
    [NotifyPropertyChangedFor(nameof(CreditStatusLabel))]
    private CreditProfileClientDto? _creditProfile;

    [ObservableProperty] private bool _isCreditProfileLoading;
    [ObservableProperty] private string? _creditProfileError;

    /// <summary>Seam determinista de tests: última carga del perfil crediticio del cliente.</summary>
    public Task? PendingCreditProfile { get; private set; }

    private int _creditProfileVersion;

    public bool HasCreditProfile => CreditProfile != null;

    public bool IsManuallyBlocked => CreditStatus == Core.Entities.CreditStatus.ManuallyBlocked;

    public string CreditStatusLabel => CreditStatus switch
    {
        Core.Entities.CreditStatus.Active => "Activo",
        Core.Entities.CreditStatus.SuspendedByMora => "Suspendido por Mora",
        Core.Entities.CreditStatus.ManuallyBlocked => "Bloqueado Manualmente",
        _ => "Sin perfil"
    };

    /// <summary>
    /// Solo Admin/Manager pueden editar el perfil. Un cliente suspendido por mora no admite
    /// escritura manual de estado (el sistema lo administra); el bloqueo manual sí es reversible.
    /// </summary>
    public bool CanEditCreditProfile => CanMutate
        && IsEditing
        && !IsDefaultCustomer
        && CreditProfile != null
        && CreditStatus != Core.Entities.CreditStatus.SuspendedByMora;

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

                StartDebouncedSearchAsync(value, newCts.Token).SafeFireAndForget("CustomerManagement.DebouncedSearch");
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

            // El perfil crediticio llega por GET aparte; se limpia cualquier estado del cliente
            // anterior antes de consultar el nuevo para no mostrar métricas cruzadas.
            ClearCreditProfileFields();
            if (!IsDefaultCustomer)
            {
                TriggerCreditProfileFetch(value.Id);
            }
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

    private async Task StartDebouncedSearchAsync(string query, CancellationToken token)
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

    // ── Perfil crediticio (Paso 11, S1/S4) ──

    private void ClearCreditProfileFields()
    {
        _creditProfileVersion++;
        PendingCreditProfile = Task.CompletedTask;
        CreditProfile = null;
        CreditProfileError = null;
        IsCreditProfileLoading = false;
        IsCreditEnabled = false;
        PaymentTermDaysText = "30";
        CreditGraceDaysText = "0";
        CreditNotes = string.Empty;
        CreditStatus = Core.Entities.CreditStatus.Active;
    }

    private void TriggerCreditProfileFetch(int customerId)
    {
        int version = ++_creditProfileVersion;
        PendingCreditProfile = LoadCreditProfileAsync(customerId, version);
    }

    private async Task LoadCreditProfileAsync(int customerId, int version)
    {
        IsCreditProfileLoading = true;
        CreditProfileError = null;

        try
        {
            var profile = await _salesService.GetCreditProfileAsync(customerId);
            if (version != _creditProfileVersion || SelectedCustomer?.Id != customerId) return;

            ApplyCreditProfile(profile);
        }
        catch (Exception ex)
        {
            if (version != _creditProfileVersion || SelectedCustomer?.Id != customerId) return;

            CreditProfile = null;
            CreditProfileError = $"No se pudo cargar el perfil crediticio: {ex.Message}";
        }
        finally
        {
            if (version == _creditProfileVersion && SelectedCustomer?.Id == customerId)
            {
                IsCreditProfileLoading = false;
            }
        }
    }

    private void ApplyCreditProfile(CreditProfileClientDto profile)
    {
        CreditProfile = profile;
        IsCreditEnabled = profile.IsCreditEnabled;
        CreditLimitText = profile.CreditLimitUSD.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);
        PaymentTermDaysText = profile.PaymentTermDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CreditGraceDaysText = profile.CreditGraceDays.ToString(System.Globalization.CultureInfo.InvariantCulture);
        CreditNotes = profile.CreditNotes ?? string.Empty;
        CreditStatus = profile.Status;
    }

    /// <summary>
    /// Valida y construye el contrato de escritura. El estado manual nunca envía
    /// SuspendedByMora: ese estado pertenece al job de mora del servidor.
    /// </summary>
    private bool TryBuildCreditProfileRequest(out CreditProfileUpdateClientDto request)
    {
        request = new CreditProfileUpdateClientDto();

        if (!decimal.TryParse(CreditLimitText, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out decimal creditLimit)
            || creditLimit < 0m)
        {
            _dialogService.ShowWarning("Perfil Crediticio", "El límite de crédito debe ser un monto no negativo.");
            return false;
        }

        if (!int.TryParse(PaymentTermDaysText, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int paymentTermDays)
            || paymentTermDays < 1 || paymentTermDays > 365)
        {
            _dialogService.ShowWarning("Perfil Crediticio", "El plazo de pago debe estar entre 1 y 365 días continuos.");
            return false;
        }

        if (!int.TryParse(CreditGraceDaysText, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out int graceDays)
            || graceDays < 0)
        {
            _dialogService.ShowWarning("Perfil Crediticio", "Los días de gracia no pueden ser negativos.");
            return false;
        }

        string? notes = string.IsNullOrWhiteSpace(CreditNotes) ? null : CreditNotes.Trim();
        if (notes is { Length: > 1000 })
        {
            _dialogService.ShowWarning("Perfil Crediticio", "Las notas crediticias no pueden exceder 1000 caracteres.");
            return false;
        }

        request = new CreditProfileUpdateClientDto
        {
            IsCreditEnabled = IsCreditEnabled,
            CreditLimitUSD = creditLimit,
            PaymentTermDays = paymentTermDays,
            CreditGraceDays = graceDays,
            CreditNotes = notes,
            Status = IsManuallyBlocked
                ? Core.Entities.CreditStatus.ManuallyBlocked
                : Core.Entities.CreditStatus.Active
        };
        return true;
    }

    [RelayCommand]
    public async Task SaveCreditProfileAsync()
    {
        if (SelectedCustomer == null || CreditProfile == null)
        {
            _dialogService.ShowWarning("Perfil Crediticio", "Seleccione un cliente para editar su perfil crediticio.");
            return;
        }

        if (!CanMutate)
        {
            _dialogService.ShowWarning("Acceso Denegado", "Los cajeros no tienen permisos para modificar el perfil crediticio.");
            return;
        }

        if (CreditStatus == Core.Entities.CreditStatus.SuspendedByMora)
        {
            _dialogService.ShowWarning("Perfil Crediticio", "El crédito está suspendido por mora y es administrado por el sistema. Regularice la deuda para rehabilitar la línea.");
            return;
        }

        if (!TryBuildCreditProfileRequest(out var request)) return;

        IsLoading = true;
        try
        {
            var updated = await _salesService.UpdateCreditProfileAsync(SelectedCustomer.Id, request);
            ApplyCreditProfile(updated);
            _dialogService.ShowSuccessDialog("Perfil crediticio actualizado correctamente.");
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Perfil Crediticio", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    public async Task ToggleCreditBlockAsync()
    {
        if (SelectedCustomer == null || CreditProfile == null)
        {
            _dialogService.ShowWarning("Perfil Crediticio", "Seleccione un cliente para modificar su bloqueo crediticio.");
            return;
        }

        if (!CanMutate)
        {
            _dialogService.ShowWarning("Acceso Denegado", "Los cajeros no tienen permisos para bloquear o desbloquear el crédito.");
            return;
        }

        if (CreditStatus == Core.Entities.CreditStatus.SuspendedByMora)
        {
            _dialogService.ShowWarning("Perfil Crediticio", "El crédito está suspendido por mora y es administrado por el sistema. Regularice la deuda para rehabilitar la línea.");
            return;
        }

        if (IsDefaultCustomer)
        {
            _dialogService.ShowWarning("Operación No Permitida", "No se puede configurar crédito para el cliente Consumidor Final.");
            return;
        }

        bool blocking = !IsManuallyBlocked;
        bool confirmed = _dialogService.ShowConfirm(
            blocking ? "Bloquear Crédito" : "Desbloquear Crédito",
            blocking
                ? $"¿Desea bloquear manualmente el crédito del cliente '{Name}'? El historial se conserva."
                : $"¿Desea desbloquear manualmente el crédito del cliente '{Name}'?");
        if (!confirmed) return;

        if (!TryBuildCreditProfileRequest(out var request)) return;
        request.Status = blocking
            ? Core.Entities.CreditStatus.ManuallyBlocked
            : Core.Entities.CreditStatus.Active;

        IsLoading = true;
        try
        {
            var updated = await _salesService.UpdateCreditProfileAsync(SelectedCustomer.Id, request);
            ApplyCreditProfile(updated);
            _dialogService.ShowSuccessDialog(blocking
                ? "Crédito bloqueado manualmente. El historial del cliente se conserva."
                : "Crédito desbloqueado correctamente.");
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Perfil Crediticio", ex.Message);
        }
        finally
        {
            IsLoading = false;
        }
    }

    public void Dispose()
    {
        var oldCts = Interlocked.Exchange(ref _searchCts, null);
        try
        {
            oldCts?.Cancel();
            oldCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        WeakReferenceMessenger.Default.UnregisterAll(this);
    }
}
