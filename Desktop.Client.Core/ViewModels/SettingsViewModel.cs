using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Desktop.Client.Services;
using Core.DTOs;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Linq;
using Core.Common;

namespace Desktop.Client.ViewModels;

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly IPaymentService _paymentService;
    private readonly ISettingsService _settingsService;
    private readonly IConnectionManager? _connectionManager;
    private EventHandler<ConnectionStatusEventArgs>? _connectionStatusHandler;
    private bool _isDialogOpen;
    private bool _disposed;

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    private string _errorMessage = string.Empty;
    public string ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    [ObservableProperty]
    private string _currentServerAddress = "http://localhost:5000/";

    [ObservableProperty]
    private string _connectionStatusText = "Conectado";

    [ObservableProperty]
    private string _connectionStatusColor = "#27AE60";

    [ObservableProperty]
    private bool _allowNegativeStock;

    public ObservableCollection<PaymentMethodDto> PaymentMethods { get; } = new();
    public ObservableCollection<TimeZoneInfo> AvailableTimeZones { get; } = new();

    private TimeZoneInfo? _selectedTimeZone;
    public TimeZoneInfo? SelectedTimeZone
    {
        get => _selectedTimeZone;
        set
        {
            if (SetProperty(ref _selectedTimeZone, value))
            {
                OnSelectedTimeZoneChangedAsync(value).SafeFireAndForget("Settings.TimeZoneChanged");
            }
        }
    }

    private readonly IDialogService? _dialogService;
    private readonly IDispatcherInvoker _dispatcherInvoker;

    public SettingsViewModel(
        IPaymentService paymentService,
        ISettingsService settingsService,
        UserSession? userSession = null,
        IDialogService? dialogService = null,
        IConnectionManager? connectionManager = null,
        IDispatcherInvoker? dispatcherInvoker = null)
    {
        _paymentService = paymentService;
        _settingsService = settingsService;
        UserSession = userSession;
        _dialogService = dialogService;
        _connectionManager = connectionManager;
        _dispatcherInvoker = dispatcherInvoker ?? new InlineDispatcherInvoker();

        if (_connectionManager != null)
        {
            CurrentServerAddress = _connectionManager.CurrentServerAddress;
            UpdateConnectionStatusDisplay(_connectionManager.Status);

            _connectionStatusHandler = OnConnectionStatusChanged;
            _connectionManager.ConnectionStatusChanged += _connectionStatusHandler;
        }

        // 8.5-W4: El constructor NO dispara fetch. EnsureLoadedAsync() es la única fuente de carga
        // (idempotente) y evita fetches duplicados en navegación al VM.
    }

    private bool _hasLoaded;

    public async Task EnsureLoadedAsync()
    {
        if (_hasLoaded) return;
        if (UserSession == null || UserSession.IsLoggedIn)
        {
            await LoadMethodsAsync();
            await LoadTimeZonesAsync();
            await LoadCurrencyFormatAsync();
            await LoadAllowNegativeStockAsync();
            _hasLoaded = true;
        }
    }

    public UserSession? UserSession { get; }

    private async Task LoadAllowNegativeStockAsync()
    {
        AllowNegativeStock = await _settingsService.GetAllowNegativeStockAsync();
    }

    [RelayCommand]
    private async Task ToggleAllowNegativeStockAsync()
    {
        try
        {
            await _settingsService.SetAllowNegativeStockAsync(AllowNegativeStock);
        }
        catch (Exception ex)
        {
            _dialogService?.ShowError("Stock Negativo", $"No se pudo guardar la configuración: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task LoadMethodsAsync()
    {
        if (UserSession != null && !UserSession.IsLoggedIn) return;

        IsLoading = true;
        ErrorMessage = string.Empty;
        PaymentMethods.Clear();

        try
        {
            var methods = await _paymentService.GetAllMethodsAsync();
            foreach (var m in methods)
            {
                PaymentMethods.Add(m);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load payment configurations: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SetActiveStatusAsync(PaymentMethodDto method)
    {
        try
        {
            await _paymentService.UpdateAsync(method);
            WeakReferenceMessenger.Default.Send(new PaymentMethodsChangedMessage());
        }
        catch (Exception ex)
        {
            if (_dialogService != null) _dialogService.ShowError("Settings Error", $"Failed to update status: {ex.Message}");
            method.IsActive = !method.IsActive; // Revert
            OnPropertyChanged(nameof(PaymentMethods));
        }
    }

    [RelayCommand]
    private async Task UpdateReferenceRequirementAsync(PaymentMethodDto method)
    {
        try
        {
            await _paymentService.UpdateAsync(method);
            WeakReferenceMessenger.Default.Send(new PaymentMethodsChangedMessage());
        }
        catch (Exception ex)
        {
            if (_dialogService != null) _dialogService.ShowError("Settings Error", $"Failed to update rule: {ex.Message}");
            method.RequiresReference = !method.RequiresReference; // Revert
            OnPropertyChanged(nameof(PaymentMethods));
        }
    }

    [RelayCommand]
    private async Task TogglePaymentTypeAsync(PaymentMethodDto method)
    {
        if (method == null) return;
        var original = method.IsCash;
        try
        {
            method.IsCash = !method.IsCash;
            var updated = await _paymentService.UpdateAsync(method);
            var index = PaymentMethods.IndexOf(method);
            if (index >= 0)
            {
                PaymentMethods[index] = new PaymentMethodDto
                {
                    Id = updated.Id,
                    Name = updated.Name,
                    IsActive = updated.IsActive,
                    RequiresReference = updated.RequiresReference,
                    IsCash = updated.IsCash,
                    DisplayOrder = updated.DisplayOrder,
                    IsDeleted = updated.IsDeleted
                };
            }
            WeakReferenceMessenger.Default.Send(new PaymentMethodsChangedMessage());
        }
        catch (Exception ex)
        {
            method.IsCash = original;
            if (_dialogService != null) _dialogService.ShowError("Error", $"Error al cambiar el tipo de método de pago: {ex.Message}");
            await LoadMethodsAsync();
        }
    }

    [RelayCommand]
    private async Task AddNewMethodAsync()
    {
        if (_dialogService == null) return;
        var newName = await _dialogService.ShowTextInputAsync(
            "Enter the name of the new Payment Method (e.g. Check, Transfer, Crypto):",
            "Method Name");

        if (string.IsNullOrWhiteSpace(newName)) return;

        if (PaymentMethods.Any(p => p.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
        {
            _dialogService.ShowWarning("Validation", "A payment method with this name already exists!");
            return;
        }

        try
        {
            var method = new PaymentMethodDto
            {
                Name = newName,
                IsActive = true,
                RequiresReference = false,
                IsCash = false // Digital por defecto
            };

            var created = await _paymentService.CreateAsync(method);
            PaymentMethods.Add(created);
            WeakReferenceMessenger.Default.Send(new PaymentMethodsChangedMessage());
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Settings Error", $"Failed to create method: {ex.Message}");
            await LoadMethodsAsync();
        }
    }

    [RelayCommand]
    private async Task RenameMethodAsync(PaymentMethodDto method)
    {
        if (method == null) return;
        string? newName = null;
        if (_dialogService != null)
        {
            newName = await _dialogService.ShowTextInputAsync(
                $"Ingrese el nuevo nombre para el método de pago '{method.Name}':",
                "Nombre del Método de Pago");
        }

        if (string.IsNullOrWhiteSpace(newName) || newName.Trim().Equals(method.Name, StringComparison.OrdinalIgnoreCase)) return;

        var cleanName = newName.Trim();
        if (PaymentMethods.Any(p => p.Id != method.Id && p.Name.Equals(cleanName, StringComparison.OrdinalIgnoreCase)))
        {
            if (_dialogService != null) _dialogService.ShowWarning("Validación", "Ya existe un método de pago con ese nombre.");
            return;
        }

        try
        {
            method.Name = cleanName;
            var updated = await _paymentService.UpdateAsync(method);
            var index = PaymentMethods.IndexOf(method);
            if (index >= 0)
            {
                PaymentMethods[index] = new PaymentMethodDto
                {
                    Id = updated.Id,
                    Name = updated.Name,
                    IsActive = updated.IsActive,
                    RequiresReference = updated.RequiresReference,
                    IsCash = updated.IsCash,
                    DisplayOrder = updated.DisplayOrder
                };
            }
            WeakReferenceMessenger.Default.Send(new PaymentMethodsChangedMessage());
        }
        catch (Exception ex)
        {
            if (_dialogService != null) _dialogService.ShowError("Error", $"Error al renombrar el método de pago: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task MoveUpAsync(PaymentMethodDto method)
    {
        if (method == null) return;
        var index = PaymentMethods.IndexOf(method);
        if (index <= 0) return; // Already at top

        var previousMethod = PaymentMethods[index - 1];
        
        // Swap positions in collection
        PaymentMethods.Move(index, index - 1);

        // Update DisplayOrder
        for (int i = 0; i < PaymentMethods.Count; i++)
        {
            PaymentMethods[i].DisplayOrder = i;
        }

        try
        {
            await _paymentService.UpdateAsync(PaymentMethods[index - 1]);
            await _paymentService.UpdateAsync(PaymentMethods[index]);
            WeakReferenceMessenger.Default.Send(new PaymentMethodsChangedMessage());
        }
        catch (Exception ex)
        {
            if (_dialogService != null) _dialogService.ShowError("Error", $"Error al reordenar métodos de pago: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task MoveDownAsync(PaymentMethodDto method)
    {
        if (method == null) return;
        var index = PaymentMethods.IndexOf(method);
        if (index < 0 || index >= PaymentMethods.Count - 1) return; // Already at bottom

        var nextMethod = PaymentMethods[index + 1];

        // Swap positions in collection
        PaymentMethods.Move(index, index + 1);

        // Update DisplayOrder
        for (int i = 0; i < PaymentMethods.Count; i++)
        {
            PaymentMethods[i].DisplayOrder = i;
        }

        try
        {
            await _paymentService.UpdateAsync(PaymentMethods[index]);
            await _paymentService.UpdateAsync(PaymentMethods[index + 1]);
            WeakReferenceMessenger.Default.Send(new PaymentMethodsChangedMessage());
        }
        catch (Exception ex)
        {
            if (_dialogService != null) _dialogService.ShowError("Error", $"Error al reordenar métodos de pago: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task DeleteMethodAsync(PaymentMethodDto method)
    {
        if (method == null) return;
        bool confirmed = _dialogService != null
            ? _dialogService.ShowConfirm(
                "Confirmar Eliminación",
                $"¿Está seguro de eliminar el método de pago '{method.Name}'?\n\nSi el método tiene transacciones históricas registradas, será archivado de forma segura sin afectar las ventas ni auditorías.")
            : false;

        if (confirmed)
        {
            try
            {
                await _paymentService.DeleteAsync(method.Id);
                PaymentMethods.Remove(method);
                WeakReferenceMessenger.Default.Send(new PaymentMethodsChangedMessage());
            }
            catch (Exception ex)
            {
                if (_dialogService != null) _dialogService.ShowError("Settings Error", $"Error al eliminar el método de pago: {ex.Message}");
                await LoadMethodsAsync();
            }
        }
    }

    private async Task LoadTimeZonesAsync()
    {
        AvailableTimeZones.Clear();
        foreach (var tz in TimeZoneInfo.GetSystemTimeZones())
        {
            AvailableTimeZones.Add(tz);
        }

        var savedTzId = await _settingsService.GetTimeZoneAsync();
        if (!string.IsNullOrEmpty(savedTzId))
        {
            SelectedTimeZone = AvailableTimeZones.FirstOrDefault(t => t.Id == savedTzId);
        }
    }

    private async Task OnSelectedTimeZoneChangedAsync(TimeZoneInfo? value)
    {
        if (value == null) return;
        try
        {
            await _settingsService.SetTimeZoneAsync(value.Id);
            WeakReferenceMessenger.Default.Send(new TimeZoneChangedMessage());
        }
        catch (Exception ex)
        {
            if (_dialogService != null) _dialogService.ShowError("Settings Error", $"Failed to save timezone: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task OpenServerConnectionAsync()
    {
        if (_dialogService == null || _isDialogOpen) return;

        _isDialogOpen = true;
        try
        {
            var saved = await _dialogService.ShowServerConnectionDialogAsync();
            if (saved && _connectionManager != null)
            {
                CurrentServerAddress = _connectionManager.CurrentServerAddress;
                UpdateConnectionStatusDisplay(_connectionManager.Status);
            }
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

    [RelayCommand]
    private async Task OpenPairingQrAsync()
    {
        if (_dialogService == null || _isDialogOpen) return;

        _isDialogOpen = true;
        try
        {
            await _dialogService.ShowPairingQrDialogAsync();
        }
        finally
        {
            _isDialogOpen = false;
        }
    }

    private void OnConnectionStatusChanged(object? sender, ConnectionStatusEventArgs e)
    {
        void Apply()
        {
            CurrentServerAddress = e.ServerAddress;
            UpdateConnectionStatusDisplay(e.Status);
        }

        if (!_dispatcherInvoker.CheckAccess())
        {
            _dispatcherInvoker.BeginInvoke(Apply);
        }
        else
        {
            Apply();
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                if (_connectionManager != null && _connectionStatusHandler != null)
                {
                    _connectionManager.ConnectionStatusChanged -= _connectionStatusHandler;
                    _connectionStatusHandler = null;
                }
            }
            _disposed = true;
        }
    }
}
