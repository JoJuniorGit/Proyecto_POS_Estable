using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class MainViewModel : ObservableObject
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

    public UserSession UserSession { get; }

    private readonly LoginViewModel _login_view_model;
    private readonly PosViewModel _pos_view_model;
    private readonly InventoryViewModel _inventory_view_model;
    private readonly SalesHistoryViewModel _sales_history_view_model;
    private readonly PendingOrdersViewModel _pending_orders_view_model;
    private readonly PendingPickupsViewModel _pending_pickups_view_model;
    private readonly SettingsViewModel _settings_view_model;
    private readonly ExchangeRateViewModel _exchange_rate_view_model;
    private readonly CashDrawerViewModel _cash_drawer_view_model;
    private readonly ImportProductsViewModel _import_products_view_model;
    private readonly DailyClosureViewModel _daily_closure_view_model;
    private readonly UsersManagementViewModel _users_management_view_model;

    private readonly IHealthPollingService _healthPollingService;
    private readonly IDialogService? _dialog_service;

    public MainViewModel(
        UserSession userSession,
        LoginViewModel login_view_model,
        PosViewModel pos_view_model,
        InventoryViewModel inventory_view_model,
        SalesHistoryViewModel sales_history_view_model,
        PendingOrdersViewModel pending_orders_view_model,
        PendingPickupsViewModel pending_pickups_view_model,
        SettingsViewModel settings_view_model,
        ExchangeRateViewModel exchange_rate_view_model,
        CashDrawerViewModel cash_drawer_view_model,
        ImportProductsViewModel import_products_view_model,
        DailyClosureViewModel daily_closure_view_model,
        UsersManagementViewModel users_management_view_model,
        IHealthPollingService healthPollingService,
        IDialogService? dialog_service = null)
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

        _healthPollingService.OnHealthRecovered += OnHealthRecovered;
        _login_view_model.LoginSuccess += OnLoginSuccess;
        UserSession.SessionChanged += OnSessionChanged;

        // Default: If not logged in, show Login view
        if (!UserSession.IsLoggedIn)
        {
            _current_view_model = _login_view_model;
            _title = "INICIO DE SESIÓN";
        }
        else
        {
            _current_view_model = _pos_view_model;
            _title = "POINT OF SALE";
        }
    }

    private void OnHealthRecovered(object? sender, System.EventArgs e)
    {
        _dialog_service?.ShowInterruptedTransactionDialog(
            "Cerrar Venta",
            "La conexión con el servidor se interrumpió durante la operación. La red ha sido restablecida. Por favor, verifique el estado de caja y presione el botón de cobro nuevamente.");
    }

    private void OnLoginSuccess()
    {
        NavigateToPos();
    }

    private void OnSessionChanged()
    {
        OnPropertyChanged(nameof(UserSession));
        if (!UserSession.IsLoggedIn)
        {
            Title = "INICIO DE SESIÓN";
            CurrentViewModel = _login_view_model;
        }
    }

    [RelayCommand]
    private void Logout()
    {
        UserSession.Logout();
    }

    [RelayCommand]
    private void NavigateToPos()
    {
        if (!UserSession.IsLoggedIn) return;
        Title = "POINT OF SALE";
        CurrentViewModel = _pos_view_model;
    }

    [RelayCommand]
    private void NavigateToInventory()
    {
        if (!UserSession.IsLoggedIn) return;
        Title = "INVENTORY";
        CurrentViewModel = _inventory_view_model;
    }

    [RelayCommand]
    private void NavigateToSalesHistory()
    {
        if (!UserSession.IsLoggedIn) return;
        Title = "SALES HISTORY";
        CurrentViewModel = _sales_history_view_model;
        _ = _sales_history_view_model.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void NavigateToPendingOrders()
    {
        if (!UserSession.IsLoggedIn) return;
        Title = "CUENTAS ABIERTAS (EN ESPERA)";
        CurrentViewModel = _pending_orders_view_model;
        _ = _pending_orders_view_model.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void NavigateToPendingPickups()
    {
        if (!UserSession.IsLoggedIn) return;
        Title = "RETIROS PENDIENTES";
        CurrentViewModel = _pending_pickups_view_model;
        _ = _pending_pickups_view_model.EnsureLoadedAsync();
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        if (!UserSession.IsLoggedIn) return;
        Title = "SYSTEM SETTINGS";
        CurrentViewModel = _settings_view_model;
    }

    [RelayCommand]
    private void NavigateToExchangeRate()
    {
        if (!UserSession.IsLoggedIn) return;
        Title = "EXCHANGE RATE";
        CurrentViewModel = _exchange_rate_view_model;
    }

    [RelayCommand]
    private void NavigateToCashDrawer()
    {
        if (!UserSession.IsLoggedIn) return;
        Title = "REGISTER / CASH DRAWER";
        CurrentViewModel = _cash_drawer_view_model;
        _ = _cash_drawer_view_model.LoadSessionAsync();
    }

    [RelayCommand]
    private void NavigateToImportProducts()
    {
        if (!UserSession.IsLoggedIn) return;
        Title = "IMPORT PRODUCTS";
        CurrentViewModel = _import_products_view_model;
    }

    [RelayCommand]
    private void NavigateToDailyClosure()
    {
        if (!UserSession.IsLoggedIn) return;
        Title = "DAILY CLOSING";
        CurrentViewModel = _daily_closure_view_model;
        _ = _daily_closure_view_model.LoadExpectedTotalsAsync();
    }

    [RelayCommand]
    private void NavigateToUsersManagement()
    {
        if (!UserSession.IsLoggedIn || !UserSession.IsAdmin) return;
        Title = "GESTIÓN DE USUARIOS";
        CurrentViewModel = _users_management_view_model;
        _ = _users_management_view_model.EnsureLoadedAsync();
    }
}
