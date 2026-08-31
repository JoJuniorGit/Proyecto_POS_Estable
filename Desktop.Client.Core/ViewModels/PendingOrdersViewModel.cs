using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Desktop.Client.Messages;
using Desktop.Client.Services;
using Core.DTOs;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using MaterialDesignThemes.Wpf;

namespace Desktop.Client.ViewModels;

public partial class PendingOrdersViewModel : ObservableObject
{
    private readonly ISalesService _salesService;
    private readonly IExchangeRateService _exchangeRateService;
    private readonly IPaymentService _paymentService;
    private readonly IDialogService _dialogService;
    private readonly UserSession? _userSession;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private decimal _currentExchangeRate = 1m;

    [ObservableProperty]
    private int? _expandedSaleId;

    [ObservableProperty]
    private string? _successMessage;

    public ObservableCollection<SaleDto> PendingSales { get; } = new();

    public IEnumerable<SaleDto> FilteredPendingSales
    {
        get
        {
            if (string.IsNullOrWhiteSpace(SearchQuery)) return PendingSales;
            var q = SearchQuery.Trim().ToLower();
            return PendingSales.Where(s =>
                (s.CustomerName ?? string.Empty).ToLower().Contains(q) ||
                (s.CustomerCedula ?? string.Empty).ToLower().Contains(q) ||
                (s.Customer?.Name ?? string.Empty).ToLower().Contains(q) ||
                (s.Customer?.CedulaOrRif ?? string.Empty).ToLower().Contains(q) ||
                s.Id.ToString().Contains(q));
        }
    }

    partial void OnSearchQueryChanged(string value) => OnPropertyChanged(nameof(FilteredPendingSales));

    public PendingOrdersViewModel(
        ISalesService salesService,
        IExchangeRateService exchangeRateService,
        IPaymentService paymentService,
        IDialogService dialogService,
        UserSession? userSession = null)
    {
        _salesService = salesService;
        _exchangeRateService = exchangeRateService;
        _paymentService = paymentService;
        _dialogService = dialogService;
        _userSession = userSession;

        PendingSales.CollectionChanged += (_, _) => OnPropertyChanged(nameof(FilteredPendingSales));

        WeakReferenceMessenger.Default.Register<OnHoldSalesRefreshMessage>(this, async (r, m) =>
        {
            var vm = (PendingOrdersViewModel)r;
            if (vm._userSession == null || vm._userSession.IsLoggedIn)
            {
                await vm.EnsureLoadedAsync();
            }
        });

        WeakReferenceMessenger.Default.Register<ExchangeRateChangedMessage>(this, async (r, m) =>
        {
            var vm = (PendingOrdersViewModel)r;
            if (vm._userSession == null || vm._userSession.IsLoggedIn)
            {
                await vm.EnsureLoadedAsync();
            }
        });
    }

    public async Task EnsureLoadedAsync()
    {
        if (_userSession != null && !_userSession.IsLoggedIn) return;

        IsLoading = true;
        SuccessMessage = null;
        try
        {
            var rateInfo = await _exchangeRateService.GetCurrentRateAsync();
            if (rateInfo.Rate > 0) CurrentExchangeRate = rateInfo.Rate;

            var list = await _salesService.GetPendingSalesAsync();
            PendingSales.Clear();
            foreach (var item in list.OrderByDescending(s => s.Date))
                PendingSales.Add(item);
        }
        catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // Ignore 401 Unauthorized when session is not logged in yet or token expired
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error al cargar cuentas abiertas: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await EnsureLoadedAsync();

    [RelayCommand]
    private void ToggleExpand(SaleDto? sale)
    {
        if (sale == null) return;
        ExpandedSaleId = ExpandedSaleId == sale.Id ? (int?)null : sale.Id;
    }

    [RelayCommand]
    private async Task LiquidarAbonarAsync(SaleDto? sale)
    {
        if (sale == null) return;
        try
        {
            var paymentMethods = new ObservableCollection<PaymentMethodDto>(
                (await _paymentService.GetActiveMethodsAsync()).ToList());

            var checkoutVm = new CheckoutViewModel(
                sale: sale,
                available_methods: paymentMethods,
                sales_service: _salesService,
                current_exchange_rate: CurrentExchangeRate,
                user_session: _userSession,
                override_sale: sale,
                dialog_service: _dialogService);

            var result = await DialogHost.Show(checkoutVm, "RootDialog");

            // Always refresh after checkout dialog closes
            await EnsureLoadedAsync();

            if (result is int invoiceId && invoiceId > 0)
                SuccessMessage = $"¡Cuenta #{sale.Id} liquidada! Factura N° {invoiceId:D5} completada.";
            else if (result is int abono && abono == -1)
                SuccessMessage = $"Abono registrado exitosamente en la cuenta #{sale.Id}.";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error al abrir cobro: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private async Task EditarAsync(SaleDto? sale)
    {
        if (sale == null) return;
        try
        {
            var (confirmed, modifiedItems) = await _dialogService.ShowEditSaleDialogAsync(sale, CurrentExchangeRate);
            if (confirmed && modifiedItems != null && modifiedItems.Any())
            {
                await _salesService.UpdateSaleItemsAsync(sale.Id, modifiedItems, CurrentExchangeRate);
                await EnsureLoadedAsync();
                SuccessMessage = $"Pedido #{sale.Id} actualizado correctamente.";
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Error al editar pedido: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}


