using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Common;
using Core.Diagnostics;
using Desktop.Client.Navigation;
using Desktop.Client.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private string _title = "Punto de Venta";
    public string Title
    {
        get => _title;
        set => SetProperty(ref _title, value);
    }

    private object _currentViewModel;
    public object CurrentViewModel
    {
        get => _currentViewModel;
        set
        {
            if (SetProperty(ref _currentViewModel, value))
            {
                StartupTimeline.Mark($"Wpf.View.{value?.GetType().Name ?? "null"}");
            }
        }
    }

    public UserSession? UserSession { get; }

    private readonly LoginViewModel? _loginViewModel;
    private PosViewModel? _posViewModel;
    private InventoryViewModel? _inventoryViewModel;
    private SalesHistoryViewModel? _salesHistoryViewModel;
    private PendingOrdersViewModel? _pendingOrdersViewModel;
    private PendingPickupsViewModel? _pendingPickupsViewModel;
    private SettingsViewModel? _settingsViewModel;
    private ExchangeRateViewModel? _exchangeRateViewModel;
    private CashDrawerViewModel? _cashDrawerViewModel;
    private ImportProductsViewModel? _importProductsViewModel;
    private SupplierInvoiceViewModel? _supplierInvoiceViewModel;
    private DailyClosureViewModel? _dailyClosureViewModel;
    private UsersManagementViewModel? _usersManagementViewModel;
    private AuthorizationNotificationViewModel? _authorizationNotificationViewModel;
    private StockKardexViewModel? _stockKardexViewModel;
    private StockCountViewModel? _stockCountViewModel;
    private CreditReceivablesViewModel? _creditReceivablesViewModel;
    private ProfitabilityViewModel? _profitabilityViewModel;

    private readonly IHealthPollingService? _healthPollingService;
    private readonly IExchangeRateService? _exchangeRateService;
    private readonly IDialogService? _dialogService;
    private readonly IServiceProvider? _serviceProvider;
    private bool _authorizationNotificationSubscribed;
    private int _disposed;
    private int _notificationDialogOpen;

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
        IExchangeRateService? exchangeRateService = null,
        SupplierInvoiceViewModel? supplierInvoiceViewModel = null,
        AuthorizationNotificationViewModel? authorizationNotificationViewModel = null,
        StockKardexViewModel? stockKardexViewModel = null,
        StockCountViewModel? stockCountViewModel = null,
        CreditReceivablesViewModel? creditReceivablesViewModel = null,
        ProfitabilityViewModel? profitabilityViewModel = null,
        IServiceProvider? serviceProvider = null)
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
        _supplierInvoiceViewModel = supplierInvoiceViewModel;
        _dailyClosureViewModel = dailyClosureViewModel;
        _usersManagementViewModel = usersManagementViewModel;
        _authorizationNotificationViewModel = authorizationNotificationViewModel;
        _stockKardexViewModel = stockKardexViewModel;
        _stockCountViewModel = stockCountViewModel;
        _creditReceivablesViewModel = creditReceivablesViewModel;
        _profitabilityViewModel = profitabilityViewModel;
        _serviceProvider = serviceProvider;
        _healthPollingService = healthPollingService
            ?? serviceProvider?.GetService(typeof(IHealthPollingService)) as IHealthPollingService;
        _dialogService = dialogService
            ?? serviceProvider?.GetService(typeof(IDialogService)) as IDialogService;
        _exchangeRateService = exchangeRateService
            ?? serviceProvider?.GetService(typeof(IExchangeRateService)) as IExchangeRateService;

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
            // Un Driver no puede abrir POS: su landing permitido es Retiros Pendientes.
            var fallbackView = WpfNavigationPolicy.GetFallbackView(UserSession?.CurrentUser?.Role);
            var pickups = ResolveViewModel(ref _pendingPickupsViewModel);
            if (fallbackView == WpfNavigationPolicy.Pickups && pickups != null)
            {
                _currentViewModel = pickups;
                _title = "RETIROS PENDIENTES";
                pickups.EnsureLoadedAsync().SafeFireAndForget("MainViewModel.InitialDriverInit");
            }
            else
            {
                var pos = ResolveViewModel(ref _posViewModel);
                _currentViewModel = pos ?? new object();
                _title = "PUNTO DE VENTA";
                if (pos != null)
                {
                    pos.InitializeForSessionAsync().SafeFireAndForget("MainViewModel.InitialPosInit");
                }
            }
        }

        // 8.150 (T10): la sesion restaurada en el arranque tambien debe activar el canal de
        // notificaciones admin si el rol es elevado. Sin sesion no se resuelve el VM de
        // notificaciones: se crea recien al loguear un rol elevado.
        if (UserSession?.IsLoggedIn == true)
        {
            SyncAuthorizationNotificationSession();
        }
    }

    private T? ResolveViewModel<T>(ref T? field) where T : class
    {
        if (field != null)
        {
            return field;
        }

        field = _serviceProvider?.GetService(typeof(T)) as T;
        return field;
    }

    private void OnHealthRecovered(object? sender, System.EventArgs e)
    {
        _dialogService?.ShowInterruptedTransactionDialog(
            "Cerrar Venta",
            "La conexión con el servidor se interrumpió durante la operación. La red ha sido restablecida. Por favor, verifique el estado de caja y presione el botón de cobro nuevamente.");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

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
        if (_authorizationNotificationViewModel != null)
        {
            _authorizationNotificationViewModel.StateChanged -= OnAuthorizationNotificationStateChanged;
            _authorizationNotificationSubscribed = false;
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
            _supplierInvoiceViewModel,
            _dailyClosureViewModel,
            _usersManagementViewModel,
            _authorizationNotificationViewModel,
            _stockKardexViewModel,
            _stockCountViewModel,
            _creditReceivablesViewModel,
            _profitabilityViewModel
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
        StartupTimeline.Mark("Wpf.Login.Success");

        var fallbackView = WpfNavigationPolicy.GetFallbackView(UserSession?.CurrentUser?.Role);
        if (fallbackView == WpfNavigationPolicy.Pickups)
        {
            // El conductor no abre POS; su vista necesita la tasa para mostrar importes.
            if (_exchangeRateService != null)
            {
                try
                {
                    await _exchangeRateService.GetCurrentRateAsync();
                }
                catch { }
            }

            NavigateToPendingPickups();
        }
        else
        {
            // POS es el dueno de su carga inicial de tasa; no bloqueamos el cambio de shell.
            NavigateToPos();
        }
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

        // 8.150 (T10, spec WPF hub lifecycle): el canal de notificaciones admin solo vive en
        // sesiones Admin/Manager; cualquier otro cambio de sesion lo desactiva y descarta la cola.
        SyncAuthorizationNotificationSession();
    }

    /// <summary>
    /// 8.150 (T10, design D7): el canal de notificaciones admin solo vive en sesiones Admin/Manager.
    /// Cualquier otra sesion lo desactiva y descarta la cola; al activarse con pendientes, el modal
    /// se abre en el hilo de UI a traves de IDialogService (mockeable).
    /// </summary>
    private void SyncAuthorizationNotificationSession()
    {
        var notificationViewModel = ResolveViewModel(ref _authorizationNotificationViewModel);
        if (notificationViewModel == null)
        {
            return;
        }

        if (!_authorizationNotificationSubscribed)
        {
            notificationViewModel.StateChanged += OnAuthorizationNotificationStateChanged;
            _authorizationNotificationSubscribed = true;
        }

        var isElevated = UserSession?.IsLoggedIn == true && (UserSession.IsAdmin || UserSession.IsManager);
        if (!isElevated)
        {
            notificationViewModel.Deactivate();
            return;
        }

        notificationViewModel.Activate();
        TryShowAuthorizationNotificationDialog();
    }

    private void OnAuthorizationNotificationStateChanged(object? sender, EventArgs e)
        => TryShowAuthorizationNotificationDialog();

    private void TryShowAuthorizationNotificationDialog()
    {
        var notificationViewModel = _authorizationNotificationViewModel;
        if (notificationViewModel == null || _dialogService == null || !notificationViewModel.HasPendingNotifications)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _notificationDialogOpen, 1, 0) != 0)
        {
            return;
        }

        ShowAuthorizationNotificationDialogAsync(notificationViewModel)
            .SafeFireAndForget("MainViewModel.ShowAuthorizationNotificationDialog");
    }

    private async Task ShowAuthorizationNotificationDialogAsync(AuthorizationNotificationViewModel notificationViewModel)
    {
        try
        {
            // La implementacion WPF bloquea en ShowDialog hasta que la cola quede vacia o el
            // aviso se disponga; el flag evita reentradas mientras el modal esta abierto.
            await _dialogService!.ShowAuthorizationNotificationAsync(notificationViewModel);
        }
        catch (Exception ex)
        {
            Core.Logging.ClientStateLogger.LogWarning(
                $"[AUTH] No se pudo mostrar la notificacion de autorizacion: {ex.Message}",
                nameof(MainViewModel));
        }
        finally
        {
            Interlocked.Exchange(ref _notificationDialogOpen, 0);
        }
    }

    [RelayCommand]
    private void Logout()
    {
        _posViewModel?.ResetSession();
        UserSession?.Logout();
    }

    /// <summary>Shared shell guard: hidden items are also rejected by the navigation commands.</summary>
    private bool CanNavigate(string view)
        => UserSession?.IsLoggedIn == true
           && WpfNavigationPolicy.CanAccess(UserSession.CurrentUser?.Role, view);

    [RelayCommand]
    private void NavigateToPos()
    {
        if (!CanNavigate(WpfNavigationPolicy.Pos) || _posViewModel == null) return;
        Title = "PUNTO DE VENTA";
        CurrentViewModel = _posViewModel;
        _posViewModel.InitializeForSessionAsync().SafeFireAndForget("MainViewModel.NavigateToPos");
    }

    [RelayCommand]
    private void NavigateToInventory()
    {
        if (!CanNavigate(WpfNavigationPolicy.Catalog) || _inventoryViewModel == null) return;
        Title = "INVENTARIO";
        CurrentViewModel = _inventoryViewModel;
        _inventoryViewModel.EnsureLoadedAsync().SafeFireAndForget("MainViewModel.NavigateToInventory");
    }

    [RelayCommand]
    private void NavigateToStockKardex()
    {
        if (!CanNavigate(WpfNavigationPolicy.Kardex) || _stockKardexViewModel == null) return;
        Title = "KARDEX / AUDITORÍA DE INVENTARIO";
        CurrentViewModel = _stockKardexViewModel;
        _stockKardexViewModel.EnsureLoadedAsync().SafeFireAndForget("MainViewModel.NavigateToStockKardex");
    }

    [RelayCommand]
    private void NavigateToStockCount()
    {
        if (!CanNavigate(WpfNavigationPolicy.StockCount) || _stockCountViewModel == null) return;
        Title = "CONTEO FÍSICO";
        CurrentViewModel = _stockCountViewModel;
        _stockCountViewModel.EnsureLoadedAsync().SafeFireAndForget("MainViewModel.NavigateToStockCount");
    }

    [RelayCommand]
    private void NavigateToSalesHistory()
    {
        if (!CanNavigate(WpfNavigationPolicy.History) || _salesHistoryViewModel == null) return;
        Title = "HISTORIAL DE VENTAS";
        CurrentViewModel = _salesHistoryViewModel;
        _salesHistoryViewModel.EnsureLoadedAsync().SafeFireAndForget("MainViewModel.NavigateToSalesHistory");
    }

    [RelayCommand]
    private void NavigateToPendingOrders()
    {
        if (!CanNavigate(WpfNavigationPolicy.Pending) || _pendingOrdersViewModel == null) return;
        Title = "CUENTAS ABIERTAS (EN ESPERA)";
        CurrentViewModel = _pendingOrdersViewModel;
        _pendingOrdersViewModel.EnsureLoadedAsync().SafeFireAndForget("MainViewModel.NavigateToPendingOrders");
    }

    [RelayCommand]
    private void NavigateToPendingPickups()
    {
        if (!CanNavigate(WpfNavigationPolicy.Pickups) || _pendingPickupsViewModel == null) return;
        Title = "RETIROS PENDIENTES";
        CurrentViewModel = _pendingPickupsViewModel;
        _pendingPickupsViewModel.EnsureLoadedAsync().SafeFireAndForget("MainViewModel.NavigateToPendingPickups");
    }

    [RelayCommand]
    private void NavigateToCreditReceivables()
    {
        if (!CanNavigate(WpfNavigationPolicy.Credit) || _creditReceivablesViewModel == null) return;
        Title = "CUENTAS POR COBRAR (CxC)";
        CurrentViewModel = _creditReceivablesViewModel;
        _creditReceivablesViewModel.EnsureLoadedAsync().SafeFireAndForget("MainViewModel.NavigateToCreditReceivables");
    }

    [RelayCommand]
    private void NavigateToProfitability()
    {
        if (!CanNavigate(WpfNavigationPolicy.Profitability) || _profitabilityViewModel == null) return;
        Title = "RENTABILIDAD";
        CurrentViewModel = _profitabilityViewModel;
        _profitabilityViewModel.EnsureLoadedAsync().SafeFireAndForget("MainViewModel.NavigateToProfitability");
    }

    [RelayCommand]
    private void NavigateToSettings()
    {
        if (!CanNavigate(WpfNavigationPolicy.Settings) || _settingsViewModel == null) return;
        Title = "CONFIGURACIÓN DEL SISTEMA";
        CurrentViewModel = _settingsViewModel;
        _settingsViewModel.EnsureLoadedAsync().SafeFireAndForget("MainViewModel.NavigateToSettings");
    }

    [RelayCommand]
    private void NavigateToExchangeRate()
    {
        if (!CanNavigate(WpfNavigationPolicy.Exchange) || _exchangeRateViewModel == null) return;
        Title = "TASA DE CAMBIO";
        CurrentViewModel = _exchangeRateViewModel;
        _exchangeRateViewModel.LoadAllCommand.ExecuteAsync(null).SafeFireAndForget("MainViewModel.NavigateToExchangeRate");
    }

    [RelayCommand]
    private void NavigateToCashDrawer()
    {
        if (!CanNavigate(WpfNavigationPolicy.Register) || _cashDrawerViewModel == null) return;
        Title = "CAJA / REGISTRO DE CAJA";
        CurrentViewModel = _cashDrawerViewModel;
        _cashDrawerViewModel.LoadSessionAsync().SafeFireAndForget("MainViewModel.NavigateToCashDrawer");
    }

    [RelayCommand]
    private void NavigateToImportProducts()
    {
        if (!CanNavigate(WpfNavigationPolicy.Import) || _importProductsViewModel == null) return;
        Title = "IMPORTACIÓN Y EXPORTACIÓN DE PRODUCTOS";
        CurrentViewModel = _importProductsViewModel;
    }

    [RelayCommand]
    private void NavigateToSupplierInvoices()
    {
        if (!CanNavigate(WpfNavigationPolicy.SupplierInvoices) || _supplierInvoiceViewModel == null) return;
        Title = "FACTURAS DE PROVEEDORES";
        CurrentViewModel = _supplierInvoiceViewModel;
    }

    [RelayCommand]
    private void NavigateToDailyClosure()
    {
        if (!CanNavigate(WpfNavigationPolicy.Closing) || _dailyClosureViewModel == null) return;
        Title = "CIERRE DIARIO";
        CurrentViewModel = _dailyClosureViewModel;
        _dailyClosureViewModel.LoadExpectedTotalsAsync().SafeFireAndForget("MainViewModel.NavigateToDailyClosure");
    }

    [RelayCommand]
    private void NavigateToUsersManagement()
    {
        if (!CanNavigate(WpfNavigationPolicy.Users) || _usersManagementViewModel == null) return;
        Title = "GESTIÓN DE USUARIOS";
        CurrentViewModel = _usersManagementViewModel;
        _usersManagementViewModel.EnsureLoadedAsync().SafeFireAndForget("MainViewModel.NavigateToUsersManagement");
    }

    private bool _isAnyModalOpen;
    public bool IsAnyModalOpen
    {
        get => _isAnyModalOpen;
        set => SetProperty(ref _isAnyModalOpen, value);
    }

    [ObservableProperty]
    private bool _isRightDrawerOpen = Array.Exists(Environment.GetCommandLineArgs(), a => a.Equals("--e2e", StringComparison.OrdinalIgnoreCase));

    public bool HasUncommittedCartItems => _posViewModel?.Cart?.HasUncommittedItems == true;

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
