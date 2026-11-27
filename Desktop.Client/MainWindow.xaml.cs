using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using Core.Common;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Desktop.Client.Views;

namespace Desktop.Client;

public partial class MainWindow : Window
{
    private static readonly IReadOnlyDictionary<Type, Func<FrameworkElement>> ViewFactories =
        new Dictionary<Type, Func<FrameworkElement>>
        {
            [typeof(LoginViewModel)] = () => new LoginView(),
            [typeof(PosViewModel)] = () => new PosView(),
            [typeof(InventoryViewModel)] = () => new InventoryView(),
            [typeof(SalesHistoryViewModel)] = () => new SalesHistoryView(),
            [typeof(SettingsViewModel)] = () => new SettingsView(),
            [typeof(ExchangeRateViewModel)] = () => new ExchangeRateView(),
            [typeof(CashDrawerViewModel)] = () => new CashDrawerView(),
            [typeof(ImportProductsViewModel)] = () => new ImportProductsView(),
            [typeof(SupplierInvoiceViewModel)] = () => new SupplierInvoiceView(),
            [typeof(StockKardexViewModel)] = () => new StockKardexView(),
            [typeof(StockCountViewModel)] = () => new StockCountView(),
            [typeof(UsersManagementViewModel)] = () => new UsersManagementView(),
            [typeof(DailyClosureViewModel)] = () => new DailyClosureView(),
            [typeof(PendingOrdersViewModel)] = () => new PendingOrdersView(),
            [typeof(PendingPickupsViewModel)] = () => new PendingPickupsView(),
            [typeof(CreditReceivablesViewModel)] = () => new CreditReceivablesView(),
            [typeof(ProfitabilityViewModel)] = () => new ProfitabilityView()
        };

    private readonly IDialogService _dialogService;
    private readonly MainViewModel _viewModel;
    private readonly Dictionary<object, FrameworkElement> _viewCache = new();

    public MainWindow(MainViewModel viewModel, IDialogService dialogService)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _dialogService = dialogService;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        ShowCurrentView();
    }

    private bool _isShuttingDown;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentViewModel))
        {
            ShowCurrentView();
        }
    }

    private void ShowCurrentView()
    {
        var current = _viewModel.CurrentViewModel;
        if (current == null)
        {
            MainContent.Content = null;
            return;
        }

        if (!_viewCache.TryGetValue(current, out var view))
        {
            if (!ViewFactories.TryGetValue(current.GetType(), out var factory))
            {
                // Fallback seguro: deja que el DataTemplate implicito de Window.Resources
                // resuelva el tipo si aparece un ViewModel nuevo sin cache explicito.
                MainContent.Content = current;
                return;
            }

            view = factory();
            view.DataContext = current;
            _viewCache[current] = view;
        }

        if (!ReferenceEquals(view.DataContext, current))
        {
            view.DataContext = current;
        }

        MainContent.Content = view;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        if (App.IsShutdownRequested && _isShuttingDown) return;

        bool hasUncommittedCart = (DataContext as MainViewModel)?.HasUncommittedCartItems == true;
        bool hasOpenModal = _dialogService.HasOpenModalDialog;

        // Si hay un diálogo modal abierto (ventana o DialogHost), avisar antes de cerrar
        // para evitar la pérdida accidental de información sin confirmar (p. ej. un adelanto a medio llenar).
        if (_dialogService.HasOpenModalDialog || hasUncommittedCart)
        {
            string message;
            if (hasOpenModal && hasUncommittedCart)
            {
                message = "Hay un diálogo abierto y productos sin cobrar en el carrito de la venta actual.\n\n" +
                    "¿Desea cerrar la aplicación de todos modos? Los datos no confirmados del diálogo se perderán y la venta en curso quedará pendiente.";
            }
            else if (hasOpenModal)
            {
                message = "Hay un diálogo abierto con información posiblemente sin guardar.\n\n" +
                    "¿Desea cerrar la aplicación de todos modos? Los datos no confirmados del diálogo se perderán.";
            }
            else
            {
                message = "Hay productos sin cobrar en el carrito de la venta actual.\n\n" +
                    "¿Desea cerrar la aplicación de todos modos? La venta en curso quedará pendiente y podrá recuperarse en el próximo inicio si el cierre no fue confirmado.";
            }

            if (!_dialogService.ShowConfirm("Confirmar cierre", message))
            {
                e.Cancel = true;
                return;
            }

            if (hasOpenModal && hasUncommittedCart)
            {
                App.ShutdownReason = "Cierre de la ventana principal con diálogo abierto y carrito con productos (confirmado por el usuario)";
            }
            else if (hasUncommittedCart)
            {
                App.ShutdownReason = "Cierre de la ventana principal con carrito con productos (confirmado por el usuario)";
            }
            else
            {
                App.ShutdownReason = "Cierre de la ventana principal con diálogo abierto (confirmado por el usuario)";
            }
        }
        else
        {
            App.ShutdownReason = "Cierre de la ventana principal";
        }

        if (!_isShuttingDown)
        {
            e.Cancel = true;
            _isShuttingDown = true;
            App.IsShutdownRequested = true;

            RunShutdownAsync().SafeFireAndForget("MainWindow.OnClosingShutdown");
        }
    }

    private async Task RunShutdownAsync()
    {
        try
        {
            if (Application.Current is App app)
            {
                await app.StopServicesAsync();
            }
        }
        finally
        {
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        MainContent.Content = null;
        _viewCache.Clear();
        (DataContext as System.IDisposable)?.Dispose();

        base.OnClosed(e);

        // Red de seguridad: con ShutdownMode=OnMainWindowClose esto es redundante, pero
        // garantiza el apagado completo de la aplicación (y de cualquier ventana restante,
        // como el escáner) aunque el modo de cierre cambie en el futuro.
        Application.Current?.Shutdown();
    }
}