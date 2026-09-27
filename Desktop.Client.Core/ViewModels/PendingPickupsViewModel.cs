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

    public bool MatchesSearch(PendingPickupClientDto? p)
    {
        if (p == null) return false;
        if (string.IsNullOrWhiteSpace(SearchQuery)) return true;
        var q = SearchQuery.Trim().ToLower();
        return (p.CustomerName ?? string.Empty).ToLower().Contains(q) ||
               (p.CustomerCedula ?? string.Empty).ToLower().Contains(q) ||
               (p.InvoiceNumber?.ToString() ?? p.SaleId.ToString()).Contains(q);
    }

    public PendingPickupsViewModel(ISalesService salesService, IDialogService dialogService)
    {
        _salesService = salesService;
        _dialogService = dialogService;
    }

    public async Task EnsureLoadedAsync()
    {
        if (IsLoading) return;

        IsLoading = true;
        SuccessMessage = null;
        try
        {
            var (list, totalCount) = await _salesService.GetPendingPickupsPagedAsync(PageSize, 0);
            _totalCount = totalCount;
            Pickups.Clear();
            var uniqueItems = (list ?? Enumerable.Empty<PendingPickupClientDto>())
                .Where(p => p != null)
                .GroupBy(p => p.SaleId)
                .Select(g => g.First())
                .OrderByDescending(p => p.Date);

            foreach (var item in uniqueItems)
            {
                Pickups.Add(item);
            }
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
        if (IsLoading || IsLoadingMore || !HasMore) return;

        IsLoadingMore = true;
        try
        {
            var (list, totalCount) = await _salesService.GetPendingPickupsPagedAsync(PageSize, Pickups.Count);
            _totalCount = totalCount;
            var uniqueItems = (list ?? Enumerable.Empty<PendingPickupClientDto>())
                .Where(p => p != null)
                .GroupBy(p => p.SaleId)
                .Select(g => g.First())
                .OrderByDescending(p => p.Date);

            foreach (var item in uniqueItems)
            {
                if (!Pickups.Any(x => x != null && x.SaleId == item.SaleId))
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
            
            var existing = Pickups.FirstOrDefault(x => x != null && x.SaleId == pickup.SaleId);
            if (existing != null)
            {
                Pickups.Remove(existing);
                _totalCount = Math.Max(0, _totalCount - 1);
                UpdateHasMore();
            }
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
