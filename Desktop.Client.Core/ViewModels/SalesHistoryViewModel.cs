using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

namespace Desktop.Client.ViewModels;

public partial class SalesHistoryViewModel : ObservableObject
{
    private readonly ISalesService _sales_service;
    private CancellationTokenSource? _search_cts;
    private CancellationTokenSource? _selection_cts;
    private bool _has_loaded;

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

    public SalesHistoryViewModel(ISalesService sales_service)
    {
        _sales_service = sales_service;

        WeakReferenceMessenger.Default.Register<TimeZoneChangedMessage>(this, (_r, _m) =>
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() => _ = LoadHistoryAsync());
        });
    }

    public async Task EnsureLoadedAsync()
    {
        if (!_has_loaded)
        {
            _has_loaded = true;
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
        _search_cts?.Cancel();
        _search_cts = new CancellationTokenSource();
        var _token = _search_cts.Token;

        IsLoading = true;
        ErrorMessage = null;

        try
        {
            var (_items, _total) = await _sales_service.GetSalesHistoryAsync(CurrentPage, PageSize, StartDate, EndDate, _token);

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
        // Cancel old request but do NOT Dispose — old tasks may still reference the token.
        _selection_cts?.Cancel();

        if (_selected_sale_item is null)
        {
            ClearSelectedDetailState();
            return;
        }

        var _current_selection_cts = new CancellationTokenSource();
        _selection_cts = _current_selection_cts;
        var _token = _current_selection_cts.Token;
        var _sale_id = _selected_sale_item.Id;

        // Immediately blank detail panel so user sees a clean loading state
        ReplaceCollection(SelectedSaleItems, Array.Empty<SaleItemHistoryDto>());
        ReplaceCollection(SelectedSalePayments, Array.Empty<PaymentDetailDto>());
        DetailSubtotalBsS = 0;
        IsDetailDebouncing = true;
        IsDetailFetching = false;
        DetailErrorMessage = null;

        try
        {
            // Debounce: wait 200ms so rapid arrow-key navigation doesn't hammer the API
            await Task.Delay(200, _token);

            if (_token.IsCancellationRequested || SelectedSale?.Id != _sale_id)
                return;

            IsDetailDebouncing = false;
            IsDetailFetching = true;

            var _detail = await _sales_service.GetSaleHistoryDetailAsync(_sale_id, _token);

            if (_token.IsCancellationRequested || SelectedSale?.Id != _sale_id)
                return;

            // Map and populate
            ReplaceCollection(SelectedSaleItems, _detail.Items);
            ReplaceCollection(SelectedSalePayments, _detail.Payments);
            DetailSubtotalBsS = _detail.Items.Sum(_i => _i.SubtotalBsS);
            IsDetailFetching = false;
        }
        catch (OperationCanceledException)
        {
            // Expected when selection changes rapidly — silently ignore
        }
        catch (Exception _ex)
        {
            if (!_token.IsCancellationRequested)
            {
                DetailErrorMessage = $"Failed to load invoice details: {_ex.Message}";
                ReplaceCollection(SelectedSaleItems, Array.Empty<SaleItemHistoryDto>());
                ReplaceCollection(SelectedSalePayments, Array.Empty<PaymentDetailDto>());
                DetailSubtotalBsS = 0;
                IsDetailFetching = false;
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
        ReplaceCollection(SelectedSaleItems, Array.Empty<SaleItemHistoryDto>());
        ReplaceCollection(SelectedSalePayments, Array.Empty<PaymentDetailDto>());
        DetailSubtotalBsS = 0;
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
