using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class PendingPickupsViewModel : ObservableObject
{
    private readonly ISalesService _salesService;
    private readonly IDialogService _dialogService;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private string? _successMessage;

    [ObservableProperty]
    private bool _hasMore;

    [ObservableProperty]
    private bool _isLoadingMore;

    private const int PageSize = 200;
    private int _totalCount;
    private bool _loaded;

    public ObservableCollection<PendingPickupClientDto> Pickups { get; } = new();

    public bool MatchesSearch(PendingPickupClientDto p)
    {
        if (string.IsNullOrWhiteSpace(SearchQuery)) return true;
        var q = SearchQuery.Trim().ToLower();
        return (p.CustomerName ?? string.Empty).ToLower().Contains(q) ||
               (p.CustomerCedula ?? string.Empty).ToLower().Contains(q) ||
               (p.InvoiceNumber?.ToString() ?? p.SaleId.ToString()).Contains(q);
    }

    partial void OnSearchQueryChanged(string value) => OnPropertyChanged(nameof(Pickups));

    public PendingPickupsViewModel(ISalesService salesService, IDialogService dialogService)
    {
        _salesService = salesService;
        _dialogService = dialogService;
        Pickups.CollectionChanged += (_, _) => OnPropertyChanged(nameof(Pickups));
    }

    public async Task EnsureLoadedAsync()
    {
        IsLoading = true;
        SuccessMessage = null;
        try
        {
            var (list, totalCount) = await _salesService.GetPendingPickupsPagedAsync(PageSize, 0);
            _totalCount = totalCount;
            Pickups.Clear();
            foreach (var item in list.OrderByDescending(p => p.Date))
                Pickups.Add(item);
            _loaded = true;
            UpdateHasMore();
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Error", $"Error al cargar retiros pendientes: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void UpdateHasMore()
    {
        HasMore = _loaded && Pickups.Count < _totalCount;
    }

    [RelayCommand]
    private async Task LoadMoreAsync()
    {
        if (IsLoadingMore || !HasMore) return;

        IsLoadingMore = true;
        try
        {
            var (list, totalCount) = await _salesService.GetPendingPickupsPagedAsync(PageSize, Pickups.Count);
            _totalCount = totalCount;
            foreach (var item in list.OrderByDescending(p => p.Date))
            {
                if (!Pickups.Any(x => x.SaleId == item.SaleId))
                {
                    Pickups.Add(item);
                }
            }
            UpdateHasMore();
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Error", $"Error al cargar más retiros: {ex.Message}");
        }
        finally
        {
            IsLoadingMore = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAsync() => await EnsureLoadedAsync();

    [RelayCommand]
    private async Task ConfirmPickupAsync(PendingPickupClientDto? pickup)
    {
        if (pickup == null) return;

        string invoiceLabel = pickup.InvoiceNumber.HasValue
            ? $"Factura N° {pickup.InvoiceNumber:D5}"
            : $"Pedido #{pickup.SaleId}";

        bool confirmed = _dialogService.ShowConfirm(
            "Confirmar Entrega",
            $"¿Confirmar la entrega de mercancía a {pickup.CustomerName}?\n{invoiceLabel}\nTotal: ${pickup.TotalUSD:N2} USD");

        if (!confirmed) return;

        IsLoading = true;
        try
        {
            await _salesService.ConfirmPickupAsync(pickup.SaleId);
            SuccessMessage = $"¡Retiro confirmado! {invoiceLabel} entregado a {pickup.CustomerName}.";
            await EnsureLoadedAsync();
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Error", $"Error al confirmar retiro: {ex.Message}");
        }
        finally
        {
            IsLoading = false;
        }
    }
}
