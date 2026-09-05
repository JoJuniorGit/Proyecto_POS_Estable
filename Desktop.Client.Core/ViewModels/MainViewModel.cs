using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;
using System;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private string _title = "Point of Sale";
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    private object _current_view_model;
    public object CurrentViewModel
    {
        get => _current_view_model;
        set => SetProperty(ref _current_view_model, value);
    }

    public UserSession? UserSession { get; }

    private readonly LoginViewModel? _login_view_model;
    private readonly PosViewModel? _pos_view_model;
    private readonly InventoryViewModel? _inventory_view_model;
    private readonly SalesHistoryViewModel? _sales_history_view_model;
    private readonly PendingOrdersViewModel? _pending_orders_view_model;
    private readonly PendingPickupsViewModel? _pending_pickups_view_model;
    private readonly SettingsViewModel? _settings_view_model;
    private readonly ExchangeRateViewModel? _exchange_rate_view_model;
    private readonly CashDrawerViewModel? _cash_drawer_view_model;
    private readonly ImportProductsViewModel? _import_products_view_model;
    private readonly DailyClosureViewModel? _daily_closure_view_model;
    private readonly UsersManagementViewModel? _users_management_view_model;

    private readonly IHealthPollingService? _healthPollingService;
    private readonly IExchangeRateService? _exchange_rate_service;
    private readonly IDialogService? _dialog_service;

    public MainViewModel(
        UserSession? userSession,
        LoginViewModel? login_view_model,
        PosViewModel? pos_view_model,
        InventoryViewModel? inventory_view_model,
        SalesHistoryViewModel? sales_history_view_model,
        PendingOrdersViewModel? pending_orders_view_model,
        PendingPickupsViewModel? pending_pickups_view_model,
        SettingsViewModel? settings_view_model,
        ExchangeRateViewModel? exchange_rate_view_model,
        CashDrawerViewModel? cash_drawer_view_model,
        ImportProductsViewModel? import_products_view_model,
        DailyClosureViewModel? daily_closure_view_model,
        UsersManagementViewModel? users_management_view_model,
        IHealthPollingService? healthPollingService = null,
        IDialogService? dialog_service = null,
        IExchangeRateService? exchange_rate_service = null)
    {
        UserSession = userSession;
        _login_view_model = login_view_model;
        _pos_view_model = pos_view_model;
        _inventory_view_model = inventory_view_model;
        _sales_history_view_model = sales_history_view_model;
        _pending_orders_view_model = pending_orders_view_model;
        _pending_pickups_view_model = pending_pickups_view_model;
        _settings_view_model = settings_view_model;
        _exchange_rate_view_model = exchange_rate_view_model;
        _cash_drawer_view_model = cash_drawer_view_model;
        _import_products_view_model = import_products_view_model;
        _daily_closure_view_model = daily_closure_view_model;
        _users_management_view_model = users_management_view_model;
        _healthPollingService = healthPollingService;
        _dialog_service = dialog_service;
        _exchange_rate_service = exchange_rate_service;

        if (_healthPollingService != null) _healthPollingService.OnHealthRecovered += OnHealthRecovered;
        if (_login_view_model != null) _login_view_model.LoginSuccess += OnLoginSuccess;
        if (UserSession != null) UserSession.SessionChanged += OnSessionChanged;

        if (UserSession != null && !UserSession.IsLoggedIn)
        {
            _current_view_model = _login_view_model ?? new object();
            _title = "INICIO DE SESIÓN";
        }
        else
        {
            _current_view_model = _pos_view_model ?? new object();
            _title = "POINT OF SALE";
            if (_pos_view_model != null)
            {
                _ = _pos_view_model.InitializeForSessionAsync();
            }
        }
    }

    private void OnHealthRecovered(object? sender, System.EventArgs e)
    {
        _dialog_service?.ShowInterruptedTransactionDialog(
            "Cerrar Venta",
            "La conexión con el servidor se interrumpió durante la operación. La red ha sido restablecida. Por favor, verifique el estado de caja y presione el botón de cobro nuevamente.");
    }

    public void Dispose()
    {
        if (_healthPollingService != null)
        {
            _healthPollingService.OnHealthRecovered -= OnHealthRecovered;
            try { _healthPollingService.StopPolling(); } catch { }
        }
        if (_login_view_model != null)
        {
            _login_view_model.LoginSuccess -= OnLoginSuccess;
        }
        if (UserSession != null)
        {
            UserSession.SessionChanged -= OnSessionChanged;
        }

        // Disponer los ViewModels hijo que implementan IDisposable (cancela sus CTSes y
        // libera las suscripciones a eventos globales). El contenedor DI invoca este Dispose
        // al finalizar la aplicación (_host.Dispose() en App.OnExit).
        var viewModels = new object?[]
        {
            _login_view_model,
            _pos_view_model,
            _inventory_view_model,
            _sales_history_view_model,
            _pending_orders_view_model,
            _pending_pickups_view_model,
            _settings_view_model,
            _exchange_rate_view_model,
            _cash_drawer_view_model,
            _import_products_view_model,
            _daily_closure_view_model,
            _users_management_view_model
        };

        foreach (var vm in viewModels)
        {
            if (vm is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }
        }
    }

    private async void OnLoginSuccess()
    {
        if (_exchange_rate_service != null)
        {
            try
            {
                await _exchange_rate_service.GetCurrentRateAsync();
            }
            catch { }
        }
        NavigateToPos();
    }

    private void OnSessionChanged()
    {
        OnPropertyChanged(nameof(UserSession));
        if (UserSession == null || !UserSession.IsLoggedIn)
        {
            Title = "INICIO DE SESIÓN";
            CurrentViewModel = _login_view_model ?? new object();
            _pos_view_model?.ResetSession();
        }
    }

    [RelayCommand]
    private void Logout()
    {
        _pos_view_model?.ResetSession();
        UserSession?.Logout();
    }

    [RelayCommand]
    private void NavigateToPos()
    {
        if (UserSession == null || !UserSession.IsLoggedIn || _pos_view_model == null) return;
        Title = "POINT OF SALE";
        CurrentViewModel = _pos_view_model;
        _ = _pos_view_model.InitializeForSessionAsync();
    }

    [RelayCommand]
    private void NavigateToInventory()
    {
        if (UserSession == null || !UserSession.IsLoggedIn || _inventory_view_model == null) return;
        Title = "INVENTORY";
        CurrentViewModel = _inventory_view_model;
        _ = _inventory_view_model.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void NavigateToSalesHistory()
    {
        if (UserSession == null || !UserSession.IsLoggedIn || _sales_history_view_model == null) return;
        Title = "SALES HISTORY";
        CurrentViewModel = _sales_history_view_model;
        _ = _sales_history_view_model.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void NavigateToPendingOrders()
    {
        if (UserSession == null || !UserSession.IsLoggedIn || _pending_orders_view_model == null) return;
        Title = "CUENTAS ABIERTAS (EN ESPERA)";
        CurrentViewModel = _pending_orders_view_model;
        _ = _pending_orders_view_model.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void NavigateToPendingPickups()
    {
        if (UserSession == null || !UserSession.IsLoggedIn || _pending_pickups_view_model == null) return;
        Title = "RETIROS PENDIENTES";
        CurrentViewModel = _pending_pickups_view_model;
        _ = _pending_pickups_view_model.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        if (UserSession == null || !UserSession.IsLoggedIn || _settings_view_model == null) return;
        Title = "SYSTEM SETTINGS";
        CurrentViewModel = _settings_view_model;
        _ = _settings_view_model.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void NavigateToExchangeRate()
    {
        if (UserSession == null || !UserSession.IsLoggedIn || _exchange_rate_view_model == null) return;
        Title = "EXCHANGE RATE";
        CurrentViewModel = _exchange_rate_view_model;
        _ = _exchange_rate_view_model.LoadAllCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void NavigateToCashDrawer()
    {
        if (UserSession == null || !UserSession.IsLoggedIn || _cash_drawer_view_model == null) return;
        Title = "REGISTER / CASH DRAWER";
        CurrentViewModel = _cash_drawer_view_model;
        _ = _cash_drawer_view_model.LoadSessionAsync();
    }

    [RelayCommand]
    private void NavigateToImportProducts()
    {
        if (UserSession == null || !UserSession.IsLoggedIn || _import_products_view_model == null) return;
        Title = "IMPORT PRODUCTS";
        CurrentViewModel = _import_products_view_model;
    }

    [RelayCommand]
    private void NavigateToDailyClosure()
    {
        if (UserSession == null || !UserSession.IsLoggedIn || _daily_closure_view_model == null) return;
        Title = "DAILY CLOSING";
        CurrentViewModel = _daily_closure_view_model;
        _ = _daily_closure_view_model.LoadExpectedTotalsAsync();
    }

    [RelayCommand]
    private void NavigateToUsersManagement()
    {
        if (UserSession == null || !UserSession.IsLoggedIn || !UserSession.IsAdmin || _users_management_view_model == null) return;
        Title = "GESTIÓN DE USUARIOS";
        CurrentViewModel = _users_management_view_model;
        _ = _users_management_view_model.EnsureLoadedAsync();
    }

    private bool _isAnyModalOpen;
    public bool IsAnyModalOpen
    {
        get => _isAnyModalOpen;
        set => SetProperty(ref _isAnyModalOpen, value);
    }

    [RelayCommand]
    private async Task OpenPairingQrAsync()
    {
        if (_dialog_service == null || IsAnyModalOpen) return;

        IsAnyModalOpen = true;
        try
        {
            await _dialog_service.ShowPairingQrDialogAsync();
        }
        finally
        {
            IsAnyModalOpen = false;
        }
    }

    [RelayCommand]
    private async Task OpenServerConnectionAsync()
    {
        if (_dialog_service == null || IsAnyModalOpen) return;

        IsAnyModalOpen = true;
        try
        {
            await _dialog_service.ShowServerConnectionDialogAsync();
        }
        finally
        {
            IsAnyModalOpen = false;
        }
    }
}
