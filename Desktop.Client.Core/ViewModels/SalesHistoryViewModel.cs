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
        // Búsqueda multicampo con debounce: al escribir, se espera 300 ms y se
        // recarga desde la primera página con el término aplicado.
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
            // Debounce cancelado por un tipeo más reciente — se ignora.
        }
    }

    private int _currentPage = 1;
    public int CurrentPage
    {
        get => _currentPage;
        set => SetProperty(ref _currentPage, value);
    }

    private int _pageSize = 25;
    public int PageSize
    {
        get => _pageSize;
        set => SetProperty(ref _pageSize, value);
    }

    private int _totalItems = 0;
    public int TotalItems
    {
        get => _totalItems;
        set => SetProperty(ref _totalItems, value);
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

        // Filtro inicial: solo el día en curso. Se asignan los campos directamente
        // (no las propiedades) para no disparar LoadHistoryAsync antes de que el
        // servicio esté listo; la carga inicial la dispara EnsureLoadedAsync.
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

            // Precarga inmediata de valores básicos disponibles en la fila de la grilla
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

            var _detail = await _salesService.GetSaleHistoryDetailAsync(_saleId, _token);

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

    // 8.6-M11/M12: VM retenido de facto singleton por MainViewModel → debe liberar sus recursos
    // (CTS de búsqueda/selección/debounce y suscripciones de mensajes) al terminar la app.
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
