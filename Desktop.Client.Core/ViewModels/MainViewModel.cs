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

    private object _currentViewModel;
    public object CurrentViewModel
    {
        get => _currentViewModel;
        set => SetProperty(ref _currentViewModel, value);
    }

    public UserSession? UserSession { get; }

    private readonly LoginViewModel? _loginViewModel;
    private readonly PosViewModel? _posViewModel;
    private readonly InventoryViewModel? _inventoryViewModel;
    private readonly SalesHistoryViewModel? _salesHistoryViewModel;
    private readonly PendingOrdersViewModel? _pendingOrdersViewModel;
    private readonly PendingPickupsViewModel? _pendingPickupsViewModel;
    private readonly SettingsViewModel? _settingsViewModel;
    private readonly ExchangeRateViewModel? _exchangeRateViewModel;
    private readonly CashDrawerViewModel? _cashDrawerViewModel;
    private readonly ImportProductsViewModel? _importProductsViewModel;
    private readonly DailyClosureViewModel? _dailyClosureViewModel;
    private readonly UsersManagementViewModel? _usersManagementViewModel;

    private readonly IHealthPollingService? _healthPollingService;
    private readonly IExchangeRateService? _exchangeRateService;
    private readonly IDialogService? _dialogService;

    public MainViewModel(
        UserSession? userSession,
        LoginViewModel? loginViewModel,
        PosViewModel? posViewModel,
        InventoryViewModel? inventoryViewModel,
        SalesHistoryViewModel? salesHistoryViewModel,
        PendingOrdersViewModel? pendingOrdersViewModel,
        PendingPickupsViewModel? pendingPickupsViewModel,
        SettingsViewModel? settingsViewModel,
        ExchangeRateViewModel? exchangeRateViewModel,
        CashDrawerViewModel? cashDrawerViewModel,
        ImportProductsViewModel? importProductsViewModel,
        DailyClosureViewModel? dailyClosureViewModel,
        UsersManagementViewModel? usersManagementViewModel,
        IHealthPollingService? healthPollingService = null,
        IDialogService? dialogService = null,
        IExchangeRateService? exchangeRateService = null)
    {
        UserSession = userSession;
        _loginViewModel = loginViewModel;
        _posViewModel = posViewModel;
        _inventoryViewModel = inventoryViewModel;
        _salesHistoryViewModel = salesHistoryViewModel;
        _pendingOrdersViewModel = pendingOrdersViewModel;
        _pendingPickupsViewModel = pendingPickupsViewModel;
        _settingsViewModel = settingsViewModel;
        _exchangeRateViewModel = exchangeRateViewModel;
        _cashDrawerViewModel = cashDrawerViewModel;
        _importProductsViewModel = importProductsViewModel;
        _dailyClosureViewModel = dailyClosureViewModel;
        _usersManagementViewModel = usersManagementViewModel;
        _healthPollingService = healthPollingService;
        _dialogService = dialogService;
        _exchangeRateService = exchangeRateService;

        if (_healthPollingService != null) _healthPollingService.OnHealthRecovered += OnHealthRecovered;
        if (_loginViewModel != null) _loginViewModel.LoginSuccess += OnLoginSuccess;
        if (UserSession != null) UserSession.SessionChanged += OnSessionChanged;

        if (UserSession != null && !UserSession.IsLoggedIn)
        {
            _currentViewModel = _loginViewModel ?? new object();
            _title = "INICIO DE SESIÓN";
        }
        else
        {
            _currentViewModel = _posViewModel ?? new object();
            _title = "POINT OF SALE";
            if (_posViewModel != null)
            {
                _ = _posViewModel.InitializeForSessionAsync();
            }
        }
    }

    private void OnHealthRecovered(object? sender, System.EventArgs e)
    {
        _dialogService?.ShowInterruptedTransactionDialog(
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
        if (_loginViewModel != null)
        {
            _loginViewModel.LoginSuccess -= OnLoginSuccess;
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
            _loginViewModel,
            _posViewModel,
            _inventoryViewModel,
            _salesHistoryViewModel,
            _pendingOrdersViewModel,
            _pendingPickupsViewModel,
            _settingsViewModel,
            _exchangeRateViewModel,
            _cashDrawerViewModel,
            _importProductsViewModel,
            _dailyClosureViewModel,
            _usersManagementViewModel
        };

        foreach (var vm in viewModels)
        {
            if (vm is IDisposable disposable)
            {
                try { disposable.Dispose(); } catch { }
            }
        }
    }

    private void OnLoginSuccess()
    {
        Core.Common.TaskExtensions.SafeFireAndForget(OnLoginSuccessAsync(), "MainViewModel.OnLoginSuccess");
    }

    private async Task OnLoginSuccessAsync()
    {
        if (_exchangeRateService != null)
        {
            try
            {
                await _exchangeRateService.GetCurrentRateAsync();
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
            CurrentViewModel = _loginViewModel ?? new object();
            _posViewModel?.ResetSession();
        }
    }

    [RelayCommand]
    private void Logout()
    {
        _posViewModel?.ResetSession();
        UserSession?.Logout();
    }

    [RelayCommand]
    private void NavigateToPos()
    {
        if (UserSession == null || !UserSession.IsLoggedIn || _posViewModel == null) return;
        Title = "POINT OF SALE";
        CurrentViewModel = _posViewModel;
        _ = _posViewModel.InitializeForSessionAsync();
    }

    [RelayCommand]
    private void NavigateToInventory()
    {
        if (UserSession == null || !UserSession.IsLoggedIn || _inventoryViewModel == null) return;
        Title = "INVENTORY";
        CurrentViewModel = _inventoryViewModel;
        _ = _inventoryViewModel.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void NavigateToSalesHistory()
    {
        if (UserSession == null || !UserSession.IsLoggedIn || _salesHistoryViewModel == null) return;
        Title = "SALES HISTORY";
        CurrentViewModel = _salesHistoryViewModel;
        _ = _salesHistoryViewModel.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void NavigateToPendingOrders()
    {
        if (UserSession == null || !UserSession.IsLoggedIn || _pendingOrdersViewModel == null) return;
        Title = "CUENTAS ABIERTAS (EN ESPERA)";
        CurrentViewModel = _pendingOrdersViewModel;
        _ = _pendingOrdersViewModel.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void NavigateToPendingPickups()
    {
        if (UserSession == null || !UserSession.IsLoggedIn || _pendingPickupsViewModel == null) return;
        Title = "RETIROS PENDIENTES";
        CurrentViewModel = _pendingPickupsViewModel;
        _ = _pendingPickupsViewModel.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        if (UserSession == null || !UserSession.IsLoggedIn || _settingsViewModel == null) return;
        Title = "SYSTEM SETTINGS";
        CurrentViewModel = _settingsViewModel;
        _ = _settingsViewModel.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void NavigateToExchangeRate()
    {
        if (UserSession == null || !UserSession.IsLoggedIn || _exchangeRateViewModel == null) return;
        Title = "EXCHANGE RATE";
        CurrentViewModel = _exchangeRateViewModel;
        _ = _exchangeRateViewModel.LoadAllCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private void NavigateToCashDrawer()
    {
        if (UserSession == null || !UserSession.IsLoggedIn || _cashDrawerViewModel == null) return;
        Title = "REGISTER / CASH DRAWER";
        CurrentViewModel = _cashDrawerViewModel;
        _ = _cashDrawerViewModel.LoadSessionAsync();
    }

    [RelayCommand]
    private void NavigateToImportProducts()
    {
        if (UserSession == null || !UserSession.IsLoggedIn || _importProductsViewModel == null) return;
        Title = "IMPORT PRODUCTS";
        CurrentViewModel = _importProductsViewModel;
    }

    [RelayCommand]
    private void NavigateToDailyClosure()
    {
        if (UserSession == null || !UserSession.IsLoggedIn || _dailyClosureViewModel == null) return;
        Title = "DAILY CLOSING";
        CurrentViewModel = _dailyClosureViewModel;
        _ = _dailyClosureViewModel.LoadExpectedTotalsAsync();
    }

    [RelayCommand]
    private void NavigateToUsersManagement()
    {
        if (UserSession == null || !UserSession.IsLoggedIn || !UserSession.IsAdmin || _usersManagementViewModel == null) return;
        Title = "GESTIÓN DE USUARIOS";
        CurrentViewModel = _usersManagementViewModel;
        _ = _usersManagementViewModel.EnsureLoadedAsync();
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
        if (_dialogService == null || IsAnyModalOpen) return;

        IsAnyModalOpen = true;
        try
        {
            await _dialogService.ShowPairingQrDialogAsync();
        }
        finally
        {
            IsAnyModalOpen = false;
        }
    }

    [RelayCommand]
    private async Task OpenServerConnectionAsync()
    {
        if (_dialogService == null || IsAnyModalOpen) return;

        IsAnyModalOpen = true;
        try
        {
            await _dialogService.ShowServerConnectionDialogAsync();
        }
        finally
        {
            IsAnyModalOpen = false;
        }
    }
}
