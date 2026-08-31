using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Desktop.Client.Services;
using Core.DTOs;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using System.Linq;

namespace Desktop.Client.ViewModels;

public partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly IPaymentService _payment_service;
    private readonly ISettingsService _settings_service;
    private readonly IConnectionManager? _connection_manager;
    private EventHandler<ConnectionStatusEventArgs>? _connection_status_handler;
    private bool _is_dialog_open;
    private bool _disposed;

    private bool _is_loading;
    public bool IsLoading
    {
        get => _is_loading;
        set => SetProperty(ref _is_loading, value);
    }

    private string _error_message = string.Empty;
    public string ErrorMessage
    {
        get => _error_message;
        set => SetProperty(ref _error_message, value);
    }

    [ObservableProperty]
    private string _currentServerAddress = "http://localhost:5000/";

    [ObservableProperty]
    private string _connectionStatusText = "Conectado";

    [ObservableProperty]
    private string _connectionStatusColor = "#27AE60";

    public ObservableCollection<PaymentMethodDto> PaymentMethods { get; } = new();
    public ObservableCollection<TimeZoneInfo> AvailableTimeZones { get; } = new();

    private TimeZoneInfo? _selected_time_zone;
    public TimeZoneInfo? SelectedTimeZone
    {
        get => _selected_time_zone;
        set
        {
            if (SetProperty(ref _selected_time_zone, value))
            {
                OnSelectedTimeZoneChanged(value);
            }
        }
    }

    private readonly IDialogService? _dialog_service;

    public SettingsViewModel(
        IPaymentService payment_service,
        ISettingsService settings_service,
        UserSession? userSession = null,
        IDialogService? dialog_service = null,
        IConnectionManager? connection_manager = null)
    {
        _payment_service = payment_service;
        _settings_service = settings_service;
        UserSession = userSession;
        _dialog_service = dialog_service;
        _connection_manager = connection_manager;

        if (_connection_manager != null)
        {
            CurrentServerAddress = _connection_manager.CurrentServerAddress;
            UpdateConnectionStatusDisplay(_connection_manager.Status);

            _connection_status_handler = OnConnectionStatusChanged;
            _connection_manager.ConnectionStatusChanged += _connection_status_handler;
        }

        if (UserSession == null || UserSession.IsLoggedIn)
        {
            _ = LoadMethodsAsync();
            _ = LoadTimeZonesAsync();
        }
    }

    public async Task EnsureLoadedAsync()
    {
        if (UserSession == null || UserSession.IsLoggedIn)
        {
            await LoadMethodsAsync();
            await LoadTimeZonesAsync();
        }
    }

    public UserSession? UserSession { get; }

    [RelayCommand]
    private async Task LoadMethodsAsync()
    {
        if (UserSession != null && !UserSession.IsLoggedIn) return;

        IsLoading = true;
        ErrorMessage = string.Empty;
        PaymentMethods.Clear();

        try
        {
            var _methods = await _payment_service.GetAllMethodsAsync();
            foreach (var _m in _methods)
            {
                PaymentMethods.Add(_m);
            }
        }
        catch (Exception _ex)
        {
            ErrorMessage = $"Failed to load payment configurations: {_ex.Message}";
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
            await _payment_service.UpdateAsync(method);
            WeakReferenceMessenger.Default.Send(new PaymentMethodsChangedMessage());
        }
        catch (Exception _ex)
        {
            MessageBox.Show($"Failed to update status: {_ex.Message}", "Settings Error", MessageBoxButton.OK, MessageBoxImage.Error);
            method.IsActive = !method.IsActive; // Revert
            OnPropertyChanged(nameof(PaymentMethods));
        }
    }

    [RelayCommand]
    private async Task UpdateReferenceRequirementAsync(PaymentMethodDto method)
    {
        try
        {
            await _payment_service.UpdateAsync(method);
            WeakReferenceMessenger.Default.Send(new PaymentMethodsChangedMessage());
        }
        catch (Exception _ex)
        {
            MessageBox.Show($"Failed to update rule: {_ex.Message}", "Settings Error", MessageBoxButton.OK, MessageBoxImage.Error);
            method.RequiresReference = !method.RequiresReference; // Revert
            OnPropertyChanged(nameof(PaymentMethods));
        }
    }

    [RelayCommand]
    private async Task AddNewMethodAsync()
    {
        if (_dialog_service == null) return;
        var _newName = await _dialog_service.ShowTextInputAsync(
            "Enter the name of the new Payment Method (e.g. Check, Transfer, Crypto):",
            "Method Name");

        if (string.IsNullOrWhiteSpace(_newName)) return;

        if (PaymentMethods.Any(p => p.Name.Equals(_newName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("A payment method with this name already exists!", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            var _method = new PaymentMethodDto
            {
                Name = _newName,
                IsActive = true,
                RequiresReference = false
            };

            var _created = await _payment_service.CreateAsync(_method);
            PaymentMethods.Add(_created);
            WeakReferenceMessenger.Default.Send(new PaymentMethodsChangedMessage());
        }
        catch (Exception _ex)
        {
            MessageBox.Show($"Failed to create method: {_ex.Message}", "Settings Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task RenameMethodAsync(PaymentMethodDto method)
    {
        if (method == null) return;
        string? newName = null;
        if (_dialog_service != null)
        {
            newName = await _dialog_service.ShowTextInputAsync(
                $"Ingrese el nuevo nombre para el método de pago '{method.Name}':",
                "Nombre del Método de Pago");
        }

        if (string.IsNullOrWhiteSpace(newName) || newName.Trim().Equals(method.Name, StringComparison.OrdinalIgnoreCase)) return;

        var cleanName = newName.Trim();
        if (PaymentMethods.Any(p => p.Id != method.Id && p.Name.Equals(cleanName, StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show("Ya existe un método de pago con ese nombre.", "Validación", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            method.Name = cleanName;
            var updated = await _payment_service.UpdateAsync(method);
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
            MessageBox.Show($"Error al renombrar el método de pago: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            await _payment_service.UpdateAsync(PaymentMethods[index - 1]);
            await _payment_service.UpdateAsync(PaymentMethods[index]);
            WeakReferenceMessenger.Default.Send(new PaymentMethodsChangedMessage());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error al reordenar métodos de pago: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
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
            await _payment_service.UpdateAsync(PaymentMethods[index]);
            await _payment_service.UpdateAsync(PaymentMethods[index + 1]);
            WeakReferenceMessenger.Default.Send(new PaymentMethodsChangedMessage());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error al reordenar métodos de pago: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task DeleteMethodAsync(PaymentMethodDto method)
    {
        var _result = MessageBox.Show($"Are you sure you want to deactivate '{method.Name}'?\n\nThis will keep historical sales intact but remove it from the Point of Sale screen.", "Confirm Deactivation", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (_result == MessageBoxResult.Yes)
        {
            try
            {
                await _payment_service.DeleteAsync(method.Id);
                var _local = PaymentMethods.FirstOrDefault(p => p.Id == method.Id);
                if (_local != null)
                {
                    _local.IsActive = false;
                    var _index = PaymentMethods.IndexOf(_local);
                    PaymentMethods[_index] = _local;
                }
                WeakReferenceMessenger.Default.Send(new PaymentMethodsChangedMessage());
            }
            catch (Exception _ex)
            {
                MessageBox.Show($"Failed to deactivate method: {_ex.Message}", "Settings Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private async Task LoadTimeZonesAsync()
    {
        AvailableTimeZones.Clear();
        foreach (var _tz in TimeZoneInfo.GetSystemTimeZones())
        {
            AvailableTimeZones.Add(_tz);
        }

        var _savedTzId = await _settings_service.GetTimeZoneAsync();
        if (!string.IsNullOrEmpty(_savedTzId))
        {
            SelectedTimeZone = AvailableTimeZones.FirstOrDefault(t => t.Id == _savedTzId);
        }
    }

    private async void OnSelectedTimeZoneChanged(TimeZoneInfo? value)
    {
        if (value == null) return;
        try
        {
            await _settings_service.SetTimeZoneAsync(value.Id);
            WeakReferenceMessenger.Default.Send(new TimeZoneChangedMessage());
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save timezone: {ex.Message}", "Settings Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task OpenServerConnectionAsync()
    {
        if (_dialog_service == null || _is_dialog_open) return;

        _is_dialog_open = true;
        try
        {
            var saved = await _dialog_service.ShowServerConnectionDialogAsync();
            if (saved && _connection_manager != null)
            {
                CurrentServerAddress = _connection_manager.CurrentServerAddress;
                UpdateConnectionStatusDisplay(_connection_manager.Status);
            }
        }
        finally
        {
            _is_dialog_open = false;
        }
    }

    [RelayCommand]
    private async Task OpenPairingQrAsync()
    {
        if (_dialog_service == null || _is_dialog_open) return;

        _is_dialog_open = true;
        try
        {
            await _dialog_service.ShowPairingQrDialogAsync();
        }
        finally
        {
            _is_dialog_open = false;
        }
    }

    private void OnConnectionStatusChanged(object? sender, ConnectionStatusEventArgs e)
    {
        void Apply()
        {
            CurrentServerAddress = e.ServerAddress;
            UpdateConnectionStatusDisplay(e.Status);
        }

        if (System.Windows.Application.Current != null && !System.Windows.Application.Current.Dispatcher.CheckAccess())
        {
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(Apply));
        }
        else
        {
            Apply();
        }
    }

    private void UpdateConnectionStatusDisplay(ConnectionStatus status)
    {
        switch (status)
        {
            case ConnectionStatus.Connected:
                ConnectionStatusText = "Conectado";
                ConnectionStatusColor = "#27AE60"; // Verde esmeralda
                break;
            case ConnectionStatus.Connecting:
                ConnectionStatusText = "Conectando...";
                ConnectionStatusColor = "#F39C12"; // Ámbar
                break;
            case ConnectionStatus.Scanning:
                ConnectionStatusText = "Buscando servidor...";
                ConnectionStatusColor = "#F39C12"; // Ámbar
                break;
            case ConnectionStatus.Disconnected:
            default:
                ConnectionStatusText = "Desconectado";
                ConnectionStatusColor = "#E74C3C"; // Rojo coral
                break;
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
                if (_connection_manager != null && _connection_status_handler != null)
                {
                    _connection_manager.ConnectionStatusChanged -= _connection_status_handler;
                    _connection_status_handler = null;
                }
            }
            _disposed = true;
        }
    }
}
