using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Core.Common;
using Desktop.Client.Services;
using Core.DTOs;
using Desktop.Client.Helpers;
using Desktop.Client.Messages;

namespace Desktop.Client.ViewModels;

public partial class SalesHistoryViewModel : ObservableObject, IDisposable
{
    private readonly ISalesService _salesService;
    private readonly Action<Action> _dispatchAction;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _selectionCts;
    private bool _hasLoaded;
    private bool _isDirty;

    private ObservableCollection<SaleHistoryDto> _sales = new();
    public ObservableCollection<SaleHistoryDto> Sales
    {
        get => _sales;
        private set => SetProperty(ref _sales, value);
    }

    private SaleHistoryDto? _selectedSale;
    public SaleHistoryDto? SelectedSale
    {
        get => _selectedSale;
        set
        {
            if (SetProperty(ref _selectedSale, value))
            {
                OnSelectedSaleChanged(value);
            }
        }
    }

    private ObservableCollection<SaleItemHistoryDto> _selectedSaleItems = new();
    public ObservableCollection<SaleItemHistoryDto> SelectedSaleItems
    {
        get => _selectedSaleItems;
        private set => SetProperty(ref _selectedSaleItems, value);
    }

    private ObservableCollection<PaymentDetailDto> _selectedSalePayments = new();
    public ObservableCollection<PaymentDetailDto> SelectedSalePayments
    {
        get => _selectedSalePayments;
        private set => SetProperty(ref _selectedSalePayments, value);
    }

    private bool _isDetailDebouncing;
    public bool IsDetailDebouncing
    {
        get => _isDetailDebouncing;
        set => SetProperty(ref _isDetailDebouncing, value);
    }

    private bool _isDetailFetching;
    public bool IsDetailFetching
    {
        get => _isDetailFetching;
        set => SetProperty(ref _isDetailFetching, value);
    }

    private string? _detailErrorMessage;
    public string? DetailErrorMessage
    {
        get => _detailErrorMessage;
        set => SetProperty(ref _detailErrorMessage, value);
    }

    private decimal _detailSubtotalBsS;
    public decimal DetailSubtotalBsS
    {
        get => _detailSubtotalBsS;
        set => SetProperty(ref _detailSubtotalBsS, value);
    }

    private decimal _detailAppliedRate;
    public decimal DetailAppliedRate
    {
        get => _detailAppliedRate;
        set => SetProperty(ref _detailAppliedRate, value);
    }

    private decimal _detailTotalUsd;
    public decimal DetailTotalUSD
    {
        get => _detailTotalUsd;
        set => SetProperty(ref _detailTotalUsd, value);
    }

    private decimal _detailTotalBsS;
    public decimal DetailTotalBsS
    {
        get => _detailTotalBsS;
        set => SetProperty(ref _detailTotalBsS, value);
    }

    private string _detailDateLocalFormatted = string.Empty;
    public string DetailDateLocalFormatted
    {
        get => _detailDateLocalFormatted;
        set => SetProperty(ref _detailDateLocalFormatted, value);
    }

    private bool _isPurchaseDetailsExpanded = true;
    public bool IsPurchaseDetailsExpanded
    {
        get => _isPurchaseDetailsExpanded;
        set => SetProperty(ref _isPurchaseDetailsExpanded, value);
    }

    private bool _isPurchaseDetailsVisible = true;
    public bool IsPurchaseDetailsVisible
    {
        get => _isPurchaseDetailsVisible;
        set => SetProperty(ref _isPurchaseDetailsVisible, value);
    }

    private DateTime? _startDate;
    public DateTime? StartDate
    {
        get => _startDate;
        set
        {
            if (SetProperty(ref _startDate, value))
            {
                OnStartDateChanged(value);
            }
        }
    }

    private DateTime? _endDate;
    public DateTime? EndDate
    {
        get => _endDate;
        set
        {
            if (SetProperty(ref _endDate, value))
            {
                OnEndDateChanged(value);
            }
        }
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                DebounceSearchAsync(value).SafeFireAndForget("SalesHistoryViewModel.DebounceSearch");
            }
        }
    }

    private CancellationTokenSource? _searchDebounceCts;

    private async Task DebounceSearchAsync(string term)
    {
        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _searchDebounceCts, newCts);
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
        }
    }

    private int _currentPage = 1;
    public int CurrentPage
    {
        get => _currentPage;
        set
        {
            if (SetProperty(ref _currentPage, value))
            {
                NotifyPaginationCanExecute();
            }
        }
    }

    private int _pageSize = 25;
    public int PageSize
    {
        get => _pageSize;
        set
        {
            if (SetProperty(ref _pageSize, value))
            {
                NotifyPaginationCanExecute();
            }
        }
    }

    private int _totalItems = 0;
    public int TotalItems
    {
        get => _totalItems;
        set
        {
            if (SetProperty(ref _totalItems, value))
            {
                NotifyPaginationCanExecute();
            }
        }
    }

    public ObservableCollection<PageNumberItem> PageNumbers { get; } = new();

    private string _pageSummary = string.Empty;
    public string PageSummary
    {
        get => _pageSummary;
        set => SetProperty(ref _pageSummary, value);
    }

    private string _targetPageInput = "1";
    public string TargetPageInput
    {
        get => _targetPageInput;
        set => SetProperty(ref _targetPageInput, value);
    }

    public int TotalPages => _totalItems > 0 ? (int)Math.Ceiling((double)_totalItems / _pageSize) : 1;

    public bool CanGoFirst => _currentPage > 1 && TotalPages > 1;
    public bool CanGoPrevious => _currentPage > 1 && TotalPages > 1;
    public bool CanGoNext => _currentPage < TotalPages && TotalPages > 1;
    public bool CanGoLast => _currentPage < TotalPages && TotalPages > 1;
    public bool CanGoToPreviousPage => CanGoPrevious;
    public bool CanGoToNextPage => CanGoNext;

    public string PaginationPageText => $"Página {_currentPage} de {TotalPages}";

    public string PaginationSummaryText
    {
        get
        {
            if (_totalItems <= 0) return "Mostrando 0 registros";
            int start = (_currentPage - 1) * _pageSize + 1;
            int end = Math.Min(_currentPage * _pageSize, _totalItems);
            return $"Mostrando {start}-{end} de {_totalItems}";
        }
    }

    public void UpdatePageNumbers()
    {
        PageNumbers.Clear();

        if (TotalPages <= 0 || _totalItems == 0)
        {
            CurrentPage = 0;
            PageSummary = "Página 0 de 0 (0 ventas)";
            TargetPageInput = "0";
            NotifyPaginationCanExecute();
            return;
        }

        if (CurrentPage <= 0) CurrentPage = 1;
        if (CurrentPage > TotalPages) CurrentPage = TotalPages;

        int startPage = Math.Max(1, CurrentPage - 2);
        int endPage = Math.Min(TotalPages, CurrentPage + 2);

        for (int p = startPage; p <= endPage; p++)
        {
            PageNumbers.Add(new PageNumberItem
            {
                PageNumber = p,
                IsActive = (p == CurrentPage)
            });
        }

        PageSummary = $"Pág. {CurrentPage} de {TotalPages} ({_totalItems} ventas)";
        TargetPageInput = CurrentPage.ToString();
        NotifyPaginationCanExecute();
    }

    public void NotifyPaginationCanExecute()
    {
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(PaginationPageText));
        OnPropertyChanged(nameof(PaginationSummaryText));
        OnPropertyChanged(nameof(CanGoFirst));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoLast));
        OnPropertyChanged(nameof(CanGoToPreviousPage));
        OnPropertyChanged(nameof(CanGoToNextPage));
    }

    private bool _isLoading = false;
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    private string? _errorMessage;
    public string? ErrorMessage
    {
        get => _errorMessage;
        set => SetProperty(ref _errorMessage, value);
    }

    private decimal _totalBsSForThePeriod;
    public decimal TotalBsSForThePeriod
    {
        get => _totalBsSForThePeriod;
        set => SetProperty(ref _totalBsSForThePeriod, value);
    }

    public SalesHistoryViewModel(ISalesService salesService, Action<Action>? dispatchAction = null)
    {
        _salesService = salesService;
        _dispatchAction = dispatchAction ?? (action => action());

        _startDate = DateTime.Today;
        _endDate = DateTime.Today;

        WeakReferenceMessenger.Default.Register<TimeZoneChangedMessage>(this, (_r, _m) =>
        {
            _dispatchAction(() => LoadHistoryAsync().SafeFireAndForget("SalesHistoryViewModel.TimeZoneChanged"));
        });

        WeakReferenceMessenger.Default.Register<SaleCompletedNotificationMessage>(this, (_r, _m) =>
        {
            _isDirty = true;
        });
    }

    public async Task EnsureLoadedAsync()
    {
        if (!_hasLoaded || _isDirty)
        {
            _hasLoaded = true;
            _isDirty = false;
            await LoadHistoryAsync();
        }
    }

    private void OnStartDateChanged(DateTime? _value) => LoadHistoryAsync().SafeFireAndForget("SalesHistoryViewModel.StartDateChanged");
    private void OnEndDateChanged(DateTime? _value) => LoadHistoryAsync().SafeFireAndForget("SalesHistoryViewModel.EndDateChanged");

    private void OnSelectedSaleChanged(SaleHistoryDto? _value)
    {
        if (_value != null)
        {
            IsPurchaseDetailsVisible = true;
            IsPurchaseDetailsExpanded = true;
            DetailDateLocalFormatted = _value.DateLocal.ToString("dd/MM/yyyy hh:mm tt", CultureInfo.CurrentCulture);
            DetailAppliedRate = _value.AppliedRate;
            DetailTotalUSD = _value.TotalUSD;
            DetailTotalBsS = _value.TotalBsS;
            DetailSubtotalBsS = 0;
            DetailErrorMessage = null;
        }

        LoadSelectedSaleDetailsWithDebounceAsync(_value).SafeFireAndForget("SalesHistoryViewModel.SelectedSaleDetails");
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
        var oldCts = Interlocked.Exchange(ref _searchCts, newCts);
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
            var (_items, _total) = await _salesService.GetSalesHistoryAsync(CurrentPage, PageSize, StartDate, EndDate, SearchText, _token);

            if (!_token.IsCancellationRequested)
            {
                Sales.Clear();
                decimal _tempTotalBsS = 0;
                foreach (var _item in _items)
                {
                    Sales.Add(_item);
                    _tempTotalBsS += _item.FinalPaidAmountBsS;
                }
                TotalItems = _total;
                TotalBsSForThePeriod = _tempTotalBsS;
                UpdatePageNumbers();
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
                ErrorMessage = $"Error al cargar historial de ventas: {_ex.Message}";
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
    private async Task FirstPageAsync()
    {
        if (CanGoFirst)
        {
            CurrentPage = 1;
            await LoadHistoryAsync();
        }
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (CanGoPrevious)
        {
            CurrentPage--;
            await LoadHistoryAsync();
        }
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (CanGoNext)
        {
            CurrentPage++;
            await LoadHistoryAsync();
        }
    }

    [RelayCommand]
    private async Task LastPageAsync()
    {
        if (CanGoLast)
        {
            CurrentPage = TotalPages;
            await LoadHistoryAsync();
        }
    }

    [RelayCommand]
    private async Task GoToPageAsync(int page)
    {
        if (page >= 1 && page <= TotalPages && page != CurrentPage)
        {
            CurrentPage = page;
            await LoadHistoryAsync();
        }
    }

    [RelayCommand]
    private async Task SubmitGoToPageAsync()
    {
        if (int.TryParse(TargetPageInput, out int target) && TotalPages > 0)
        {
            int clamped = Math.Clamp(target, 1, TotalPages);
            if (clamped != CurrentPage)
            {
                CurrentPage = clamped;
                await LoadHistoryAsync();
            }
            else
            {
                TargetPageInput = CurrentPage.ToString();
            }
        }
        else
        {
            TargetPageInput = CurrentPage > 0 ? CurrentPage.ToString() : "1";
        }
    }

    private async Task LoadSelectedSaleDetailsWithDebounceAsync(SaleHistoryDto? _selectedSaleItem)
    {
        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _selectionCts, newCts);
        try
        {
            oldCts?.Cancel();
            oldCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        if (_selectedSaleItem is null)
        {
            ClearSelectedDetailState();
            return;
        }

        var _token = newCts.Token;
        var _saleId = _selectedSaleItem.Id;

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
            await Task.Delay(200, _token);

            if (_token.IsCancellationRequested)
                return;

            IsDetailDebouncing = false;
            IsDetailFetching = true;

            var _detail = await _salesService.GetSaleHistoryDetailAsync(_saleId, _token);

            if (_token.IsCancellationRequested)
                return;

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

    public void Dispose()
    {
        foreach (var cts in new[] { _searchCts, _selectionCts, _searchDebounceCts })
        {
            if (cts == null) continue;
            cts.Cancel();
            cts.Dispose();
        }
        _searchCts = null;
        _selectionCts = null;
        _searchDebounceCts = null;
        WeakReferenceMessenger.Default.UnregisterAll(this);
        GC.SuppressFinalize(this);
    }
}
