using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Desktop.Client.Services;
using Core.DTOs;
using Desktop.Client.Helpers;
using Desktop.Client.Messages;

namespace Desktop.Client.ViewModels;

public partial class SalesHistoryViewModel : ObservableObject
{
    private readonly ISalesService _sales_service;
    private readonly Action<Action> _dispatchAction;
    private CancellationTokenSource? _search_cts;
    private CancellationTokenSource? _selection_cts;
    private bool _has_loaded;
    private bool _is_dirty;

    private ObservableCollection<SaleHistoryDto> _sales = new();
    public ObservableCollection<SaleHistoryDto> Sales
    {
        get => _sales;
        private set => SetProperty(ref _sales, value);
    }

    private SaleHistoryDto? _selected_sale;
    public SaleHistoryDto? SelectedSale
    {
        get => _selected_sale;
        set
        {
            if (SetProperty(ref _selected_sale, value))
            {
                OnSelectedSaleChanged(value);
            }
        }
    }

    private ObservableCollection<SaleItemHistoryDto> _selected_sale_items = new();
    public ObservableCollection<SaleItemHistoryDto> SelectedSaleItems
    {
        get => _selected_sale_items;
        private set => SetProperty(ref _selected_sale_items, value);
    }

    private ObservableCollection<PaymentDetailDto> _selected_sale_payments = new();
    public ObservableCollection<PaymentDetailDto> SelectedSalePayments
    {
        get => _selected_sale_payments;
        private set => SetProperty(ref _selected_sale_payments, value);
    }

    private bool _is_detail_debouncing;
    public bool IsDetailDebouncing
    {
        get => _is_detail_debouncing;
        set => SetProperty(ref _is_detail_debouncing, value);
    }

    private bool _is_detail_fetching;
    public bool IsDetailFetching
    {
        get => _is_detail_fetching;
        set => SetProperty(ref _is_detail_fetching, value);
    }

    private string? _detail_error_message;
    public string? DetailErrorMessage
    {
        get => _detail_error_message;
        set => SetProperty(ref _detail_error_message, value);
    }

    private decimal _detail_subtotal_bs_s;
    public decimal DetailSubtotalBsS
    {
        get => _detail_subtotal_bs_s;
        set => SetProperty(ref _detail_subtotal_bs_s, value);
    }

    private decimal _detail_applied_rate;
    public decimal DetailAppliedRate
    {
        get => _detail_applied_rate;
        set => SetProperty(ref _detail_applied_rate, value);
    }

    private decimal _detail_total_usd;
    public decimal DetailTotalUSD
    {
        get => _detail_total_usd;
        set => SetProperty(ref _detail_total_usd, value);
    }

    private decimal _detail_total_bs_s;
    public decimal DetailTotalBsS
    {
        get => _detail_total_bs_s;
        set => SetProperty(ref _detail_total_bs_s, value);
    }

    private string _detail_date_local_formatted = string.Empty;
    public string DetailDateLocalFormatted
    {
        get => _detail_date_local_formatted;
        set => SetProperty(ref _detail_date_local_formatted, value);
    }

    private bool _is_purchase_details_expanded = true;
    public bool IsPurchaseDetailsExpanded
    {
        get => _is_purchase_details_expanded;
        set => SetProperty(ref _is_purchase_details_expanded, value);
    }

    private bool _is_purchase_details_visible = true;
    public bool IsPurchaseDetailsVisible
    {
        get => _is_purchase_details_visible;
        set => SetProperty(ref _is_purchase_details_visible, value);
    }

    private DateTime? _start_date;
    public DateTime? StartDate
    {
        get => _start_date;
        set
        {
            if (SetProperty(ref _start_date, value))
            {
                OnStartDateChanged(value);
            }
        }
    }

    private DateTime? _end_date;
    public DateTime? EndDate
    {
        get => _end_date;
        set
        {
            if (SetProperty(ref _end_date, value))
            {
                OnEndDateChanged(value);
            }
        }
    }

    private string _search_text = string.Empty;
    public string SearchText
    {
        get => _search_text;
        set
        {
            if (SetProperty(ref _search_text, value))
            {
                _ = DebounceSearchAsync(value);
            }
        }
    }

    private CancellationTokenSource? _search_debounce_cts;

    private async Task DebounceSearchAsync(string term)
    {
        // Búsqueda multicampo con debounce: al escribir, se espera 300 ms y se
        // recarga desde la primera página con el término aplicado.
        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _search_debounce_cts, newCts);
        try
        {
            oldCts?.Cancel();
            oldCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        var token = newCts.Token;
        try
        {
            await Task.Delay(300, token);
            if (token.IsCancellationRequested) return;
            CurrentPage = 1;
            await LoadHistoryAsync();
        }
        catch (OperationCanceledException)
        {
            // Debounce cancelado por un tipeo más reciente — se ignora.
        }
    }

    private int _current_page = 1;
    public int CurrentPage
    {
        get => _current_page;
        set => SetProperty(ref _current_page, value);
    }

    private int _page_size = 25;
    public int PageSize
    {
        get => _page_size;
        set => SetProperty(ref _page_size, value);
    }

    private int _total_items = 0;
    public int TotalItems
    {
        get => _total_items;
        set => SetProperty(ref _total_items, value);
    }

    private bool _is_loading = false;
    public bool IsLoading
    {
        get => _is_loading;
        set => SetProperty(ref _is_loading, value);
    }

    private string? _error_message;
    public string? ErrorMessage
    {
        get => _error_message;
        set => SetProperty(ref _error_message, value);
    }

    private decimal _total_bs_s_for_the_period;
    public decimal TotalBsSForThePeriod
    {
        get => _total_bs_s_for_the_period;
        set => SetProperty(ref _total_bs_s_for_the_period, value);
    }

    public SalesHistoryViewModel(ISalesService sales_service, Action<Action>? dispatchAction = null)
    {
        _sales_service = sales_service;
        _dispatchAction = dispatchAction ?? (action =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
                action();
            else
                dispatcher.Invoke(action);
        });

        // Filtro inicial: solo el día en curso. Se asignan los campos directamente
        // (no las propiedades) para no disparar LoadHistoryAsync antes de que el
        // servicio esté listo; la carga inicial la dispara EnsureLoadedAsync.
        _start_date = DateTime.Today;
        _end_date = DateTime.Today;

        WeakReferenceMessenger.Default.Register<TimeZoneChangedMessage>(this, (_r, _m) =>
        {
            _dispatchAction(() => _ = LoadHistoryAsync());
        });

        WeakReferenceMessenger.Default.Register<SaleCompletedNotificationMessage>(this, (_r, _m) =>
        {
            _is_dirty = true;
        });
    }

    public async Task EnsureLoadedAsync()
    {
        if (!_has_loaded || _is_dirty)
        {
            _has_loaded = true;
            _is_dirty = false;
            await LoadHistoryAsync();
        }
    }

    private void OnStartDateChanged(DateTime? _value) => _ = LoadHistoryAsync();
    private void OnEndDateChanged(DateTime? _value) => _ = LoadHistoryAsync();

    private void OnSelectedSaleChanged(SaleHistoryDto? _value)
    {
        if (_value != null)
        {
            IsPurchaseDetailsVisible = true;
            IsPurchaseDetailsExpanded = true;

            // Precarga inmediata de valores básicos disponibles en la fila de la grilla
            DetailDateLocalFormatted = _value.DateLocal.ToString("dd/MM/yyyy hh:mm tt", CultureInfo.CurrentCulture);
            DetailAppliedRate = _value.AppliedRate;
            DetailTotalUSD = _value.TotalUSD;
            DetailTotalBsS = _value.TotalBsS;
            DetailSubtotalBsS = 0;
            DetailErrorMessage = null;
        }

        _ = LoadSelectedSaleDetailsWithDebounceAsync(_value);
    }

    [RelayCommand]
    private void TogglePurchaseDetailsPanel()
    {
        IsPurchaseDetailsExpanded = !IsPurchaseDetailsExpanded;
    }

    [RelayCommand]
    private void ClosePurchaseDetails()
    {
        IsPurchaseDetailsVisible = false;
    }

    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _search_cts, newCts);
        try
        {
            oldCts?.Cancel();
            oldCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        var _token = newCts.Token;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var (_items, _total) = await _sales_service.GetSalesHistoryAsync(CurrentPage, PageSize, StartDate, EndDate, SearchText, _token);

            if (!_token.IsCancellationRequested)
            {
                Sales.Clear();
                decimal _temp_total_bs_s = 0;
                foreach (var _item in _items)
                {
                    Sales.Add(_item);
                    _temp_total_bs_s += _item.FinalPaidAmountBsS;
                }
                TotalItems = _total;
                TotalBsSForThePeriod = _temp_total_bs_s;
                SelectedSale = null;
                ClearSelectedDetailState();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception _ex)
        {
            if (!_token.IsCancellationRequested)
            {
                ErrorMessage = $"Failed to load history: {_ex.Message}";
            }
        }
        finally
        {
            if (!_token.IsCancellationRequested)
            {
                IsLoading = false;
            }
        }
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (CurrentPage * PageSize < TotalItems)
        {
            CurrentPage++;
            await LoadHistoryAsync();
        }
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (CurrentPage > 1)
        {
            CurrentPage--;
            await LoadHistoryAsync();
        }
    }

    private async Task LoadSelectedSaleDetailsWithDebounceAsync(SaleHistoryDto? _selected_sale_item)
    {
        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _selection_cts, newCts);
        try
        {
            oldCts?.Cancel();
            oldCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        if (_selected_sale_item is null)
        {
            ClearSelectedDetailState();
            return;
        }

        var _token = newCts.Token;
        var _sale_id = _selected_sale_item.Id;

        // Limpiar colecciones de detalle mientras se realiza la petición
        _dispatchAction(() =>
        {
            ReplaceCollection(SelectedSaleItems, Array.Empty<SaleItemHistoryDto>());
            ReplaceCollection(SelectedSalePayments, Array.Empty<PaymentDetailDto>());
        });
        IsDetailDebouncing = true;
        IsDetailFetching = false;
        DetailErrorMessage = null;

        try
        {
            // Debounce: 200ms para evitar sobrecargar la API al navegar rápidamente con el teclado
            await Task.Delay(200, _token);

            if (_token.IsCancellationRequested)
                return;

            IsDetailDebouncing = false;
            IsDetailFetching = true;

            var _detail = await _sales_service.GetSaleHistoryDetailAsync(_sale_id, _token);

            if (_token.IsCancellationRequested)
                return;

            // Despacho seguro en hilo UI y actualización completa del detalle
            _dispatchAction(() =>
            {
                ReplaceCollection(SelectedSaleItems, _detail.Items);
                ReplaceCollection(SelectedSalePayments, _detail.Payments);
                DetailDateLocalFormatted = _detail.DateLocal.ToString("dd/MM/yyyy hh:mm tt", CultureInfo.CurrentCulture);
                DetailAppliedRate = _detail.AppliedRate;
                DetailTotalUSD = _detail.TotalUSD;
                DetailTotalBsS = _detail.TotalBsS;
                DetailSubtotalBsS = _detail.Items.Sum(_i => _i.SubtotalBsS);
                IsDetailFetching = false;
            });
        }
        catch (OperationCanceledException)
        {
            // Cancelado normalmente por una selección posterior — ignorar
        }
        catch (Exception _ex)
        {
            if (!_token.IsCancellationRequested)
            {
                _dispatchAction(() =>
                {
                    DetailErrorMessage = $"Error al cargar detalle de factura: {_ex.Message}";
                    ReplaceCollection(SelectedSaleItems, Array.Empty<SaleItemHistoryDto>());
                    ReplaceCollection(SelectedSalePayments, Array.Empty<PaymentDetailDto>());
                    DetailSubtotalBsS = 0;
                    IsDetailFetching = false;
                });
            }
        }
    }

    private void ClearSelectedDetailState()
    {
        IsDetailDebouncing = false;
        IsDetailFetching = false;
        DetailErrorMessage = null;
        IsPurchaseDetailsVisible = true;
        IsPurchaseDetailsExpanded = true;
        DetailAppliedRate = 0;
        DetailTotalUSD = 0;
        DetailTotalBsS = 0;
        DetailDateLocalFormatted = string.Empty;
        DetailSubtotalBsS = 0;
        _dispatchAction(() =>
        {
            ReplaceCollection(SelectedSaleItems, Array.Empty<SaleItemHistoryDto>());
            ReplaceCollection(SelectedSalePayments, Array.Empty<PaymentDetailDto>());
        });
    }

    private static void ReplaceCollection<T>(ObservableCollection<T> _target, IEnumerable<T> _source)
    {
        _target.Clear();
        foreach (var _item in _source)
        {
            _target.Add(_item);
        }
    }
}
