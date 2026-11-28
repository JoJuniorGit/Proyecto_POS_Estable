using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Desktop.Client.Messages;
using Desktop.Client.Services;
using Core.DTOs;
using Core.Entities;
using Core.Diagnostics;
using Core.Logging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class PendingOrdersViewModel : ObservableObject, IDisposable
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
    [NotifyPropertyChangedFor(nameof(ExchangeRateDisplay))]
    private decimal _currentExchangeRate = 1m;

    [ObservableProperty]
    private int? _expandedSaleId;

    [ObservableProperty]
    private string? _successMessage;

    [ObservableProperty]
    private bool _hasMore;

    [ObservableProperty]
    private bool _isLoadingMore;

    private const int PageSize = 200;
    private int _totalCount;
    private bool _loaded;
    private int _loadGeneration;
    private bool _reloadRequested;

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

    partial void OnSearchQueryChanged(string value) => NotifyListPresentation();

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(ShowEmptyState));

    public int PendingCount => PendingSales.Count;
    public int VisibleCount => FilteredPendingSales.Count();
    public decimal FilteredTotalLocal => FilteredPendingSales.Sum(item => item.TotalBsS);
    public decimal FilteredTotalUsd => FilteredPendingSales.Sum(item => item.EffectiveTotalUSD);
    public bool ShowEmptyState => _loaded && !IsLoading && VisibleCount == 0;
    public string EmptyStateTitle => PendingSales.Count == 0
        ? "No hay pedidos en espera"
        : "Sin resultados para tu búsqueda";
    public string EmptyStateSubtitle => PendingSales.Count == 0
        ? "Los pedidos pendientes de pago aparecerán aquí."
        : "Prueba con otro cliente, cédula/RIF o número de pedido.";
    public string VisibleCountText => VisibleCount == 1
        ? "Mostrando 1 pedido en espera"
        : $"Mostrando {VisibleCount} pedidos en espera";
    public string ExchangeRateDisplay => CurrencyDisplay.Number(CurrentExchangeRate, 2);

    private void NotifyListPresentation()
    {
        OnPropertyChanged(nameof(FilteredPendingSales));
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(VisibleCount));
        OnPropertyChanged(nameof(FilteredTotalLocal));
        OnPropertyChanged(nameof(FilteredTotalUsd));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateSubtitle));
        OnPropertyChanged(nameof(VisibleCountText));
    }

    public bool CanForceRelease => _userSession?.IsAdmin == true || _userSession?.IsManager == true;

    public bool CanCancelOrders => _userSession?.CurrentUser?.Role is UserRole.Admin or UserRole.Manager or UserRole.Cashier;

    private bool CanCancelOrder(SaleDto? sale) => CanCancelOrders && CanActOnOrder(sale);

    private bool CanActOnOrder(SaleDto? sale) => sale != null && (sale.ClaimedByUserId == null || sale.ClaimedByUserId == _userSession?.CurrentUser?.Id);

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

        PendingSales.CollectionChanged += (_, _) => NotifyListPresentation();

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
        if (IsLoading)
        {
            _reloadRequested = true;
            return;
        }

        IsLoading = true;
        var generation = ++_loadGeneration;
        SuccessMessage = null;
        try
        {
            StartupTimeline.Mark("Wpf.PendingOrders.Load.Start");
            var rateTask = _exchangeRateService.GetCurrentRateAsync();
            var salesTask = _salesService.GetPendingSalesPagedAsync(PageSize, 0);
            await Task.WhenAll(rateTask, salesTask);

            var rateInfo = await rateTask;
            if (rateInfo.Rate > 0) CurrentExchangeRate = rateInfo.Rate;

            var (list, totalCount) = await salesTask;
            if (generation != _loadGeneration) return;
            _totalCount = totalCount;
            PendingSales.Clear();
            foreach (var item in list.OrderByDescending(s => s.Date))
                PendingSales.Add(item);
            _loaded = true;
            NotifyListPresentation();
            UpdateHasMore();
            LiquidarAbonarCommand.NotifyCanExecuteChanged();
            EditarCommand.NotifyCanExecuteChanged();
            AnularPedidoCommand.NotifyCanExecuteChanged();
        }
        catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Error", $"Error al cargar pedidos guardados: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
            if (_reloadRequested)
            {
                _reloadRequested = false;
                await EnsureLoadedAsync();
            }
        }
    }

    private void UpdateHasMore()
    {
        HasMore = _loaded && PendingSales.Count < _totalCount;
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (_userSession != null && !_userSession.IsLoggedIn) return;
        if (IsLoading || IsLoadingMore || !HasMore) return;

        IsLoadingMore = true;
        var generation = ++_loadGeneration;
        try
        {
            var (list, totalCount) = await _salesService.GetPendingSalesPagedAsync(PageSize, PendingSales.Count);
            if (generation != _loadGeneration) return;
            _totalCount = totalCount;
            foreach (var item in list.OrderByDescending(s => s.Date))
            {
                if (!PendingSales.Any(p => p.Id == item.Id))
                {
                    PendingSales.Add(item);
                }
            }
            UpdateHasMore();
            LiquidarAbonarCommand.NotifyCanExecuteChanged();
            EditarCommand.NotifyCanExecuteChanged();
            AnularPedidoCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Error", $"Error al cargar más pedidos: {ex.Message}");
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await EnsureLoadedAsync();

    [RelayCommand]
    private void ClearSearch() => SearchQuery = string.Empty;

    [RelayCommand]
    private void ToggleExpand(SaleDto? sale)
    {
        if (sale == null) return;
        ExpandedSaleId = ExpandedSaleId == sale.Id ? (int?)null : sale.Id;
    }

    [RelayCommand(CanExecute = nameof(CanActOnOrder))]
    private async Task LiquidarAbonarAsync(SaleDto? sale)
    {
        if (!CanActOnOrder(sale)) return;

        try
        {
            await _salesService.ClaimSaleAsync(sale!.Id, "Checkout");
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Pedido bloqueado", ex.Message);
            await EnsureLoadedAsync();
            return;
        }

        object? result = null;
        CheckoutViewModel? checkoutVm = null;
        try
        {
            var paymentMethods = new ObservableCollection<PaymentMethodDto>(
                (await _paymentService.GetActiveMethodsAsync()).ToList());

            checkoutVm = new CheckoutViewModel(
                sale: sale!,
                availableMethods: paymentMethods,
                salesService: _salesService,
                currentExchangeRate: CurrentExchangeRate,
                userSession: _userSession,
                overrideSale: sale,
                dialogService: _dialogService);

            result = await _dialogService.ShowModalAsync(checkoutVm, "RootDialog");
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Error", $"Error al abrir cobro: {ex.Message}");
        }
        finally
        {
            try
            {
                await _salesService.ReleaseSaleAsync(sale!.Id);
            }
            catch (Exception ex)
            {
                ClientStateLogger.LogWarning($"[LOCK] Failed to release claim on sale #{sale?.Id}: {ex.Message}", nameof(PendingOrdersViewModel));
            }
            checkoutVm?.Dispose();
            await EnsureLoadedAsync();
            if (result is int invoiceId && invoiceId > 0)
                SuccessMessage = $"¡Cuenta #{sale!.Id} liquidada! Factura N° {invoiceId:D5} completada.";
            else if (result is int abono && abono == -1)
                SuccessMessage = $"Abono registrado exitosamente en la cuenta #{sale!.Id}.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanActOnOrder))]
    private async Task EditarAsync(SaleDto? sale)
    {
        if (!CanActOnOrder(sale)) return;

        try
        {
            await _salesService.ClaimSaleAsync(sale!.Id, "Editing");
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Pedido bloqueado", ex.Message);
            await EnsureLoadedAsync();
            return;
        }

        bool updated = false;
        try
        {
            var (confirmed, modifiedItems) = await _dialogService.ShowEditSaleDialogAsync(sale!, CurrentExchangeRate);
            if (confirmed && modifiedItems != null && modifiedItems.Any())
            {
                await _salesService.UpdateSaleItemsAsync(sale!.Id, modifiedItems, CurrentExchangeRate);
                updated = true;
            }
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Error", $"Error al editar pedido: {ex.Message}");
        }
        finally
        {
            try
            {
                await _salesService.ReleaseSaleAsync(sale!.Id);
            }
            catch (Exception ex)
            {
                ClientStateLogger.LogWarning($"[LOCK] Failed to release claim on sale #{sale?.Id}: {ex.Message}", nameof(PendingOrdersViewModel));
            }
            await EnsureLoadedAsync();
            if (updated)
                SuccessMessage = $"Pedido #{sale!.Id} actualizado correctamente.";
        }
    }

    [RelayCommand(CanExecute = nameof(CanCancelOrder))]
    private async Task AnularPedidoAsync(SaleDto? sale)
    {
        if (!CanCancelOrder(sale)) return;

        var customer = string.IsNullOrWhiteSpace(sale!.CustomerName) ? "Cliente general" : sale.CustomerName;
        var amount = CurrencyDisplay.FormatSecondary(sale.TotalBsS);
        if (!_dialogService.ShowConfirm(
                "Anular pedido",
                $"¿Anular el pedido #{sale.Id} del cliente '{customer}' por {amount}? " +
                "El pedido se descartará de los pedidos guardados."))
        {
            return;
        }

        try
        {
            await _salesService.CancelSaleAsync(sale.Id);
            await EnsureLoadedAsync();
            SuccessMessage = $"Pedido #{sale.Id} anulado correctamente.";
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Anular pedido", $"No se pudo anular el pedido: {ex.Message}");
        }
    }
    [RelayCommand(CanExecute = nameof(CanForceRelease))]
    private async Task LiberarBloqueoAsync(SaleDto? sale)
    {
        if (sale == null || !CanForceRelease) return;

        string? releaseMessage = null;
        try
        {
            await _salesService.ReleaseSaleAsync(sale.Id, force: true);
            releaseMessage = $"Bloqueo del pedido #{sale.Id} liberado.";
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Error", $"Error al liberar bloqueo: {ex.Message}");
        }
        finally
        {
            await EnsureLoadedAsync();
            if (releaseMessage != null) SuccessMessage = releaseMessage;
        }
    }

    public void Dispose()
    {
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }
}


