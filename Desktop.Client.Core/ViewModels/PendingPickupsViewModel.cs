using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class PendingPickupsViewModel : ObservableObject
{
    private readonly ISalesService _salesService;
    private readonly IDialogService _dialogService;
    private readonly Action<DeliveryReceiptClientDto, byte[]> _openDeliveryNote;

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

    public PendingPickupsViewModel(
        ISalesService salesService,
        IDialogService dialogService,
        Action<DeliveryReceiptClientDto, byte[]>? openDeliveryNote = null)
    {
        _salesService = salesService;
        _dialogService = dialogService;
        _openDeliveryNote = openDeliveryNote ?? SaveAndOpenDeliveryNote;
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

        var dialogResult = await _dialogService.ShowPartialDeliveryDialogAsync(pickup);
        var requestedItems = dialogResult?.Items?
            .Where(item => item is not null && item.Quantity > 0m)
            .ToList();
        if (requestedItems is not { Count: > 0 }) return;

        string invoiceLabel = pickup.InvoiceNumber.HasValue
            ? $"Factura N° {pickup.InvoiceNumber:D5}"
            : $"Pedido #{pickup.SaleId}";

        IsLoading = true;
        try
        {
            var receipt = await _salesService.DeliverPartialAsync(
                pickup.SaleId,
                requestedItems,
                dialogResult!.Notes,
                Guid.NewGuid().ToString("N"));

            if (string.Equals(receipt.DeliveryStatus, "Delivered", StringComparison.Ordinal))
            {
                var existing = Pickups.FirstOrDefault(x => x != null && x.SaleId == pickup.SaleId);
                if (existing != null)
                {
                    Pickups.Remove(existing);
                    _totalCount = Math.Max(0, _totalCount - 1);
                    UpdateHasMore();
                }
            }
            else
            {
                IsLoading = false;
                await EnsureLoadedAsync();
            }

            SuccessMessage = $"Retiro registrado para {invoiceLabel} de {pickup.CustomerName}.";

            if (_dialogService.ShowConfirm(
                "Nota de Despacho",
                "Retiro registrado. ¿Desea imprimir la Nota de Despacho?"))
            {
                await PrintDeliveryNoteAsync(receipt);
            }
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Error", ex.Message);
            IsLoading = false;
            await EnsureLoadedAsync();

            var refreshedPickup = Pickups.FirstOrDefault(x => x != null && x.SaleId == pickup.SaleId);
            if (refreshedPickup != null)
            {
                refreshedPickup.PendingDraft = new PartialDeliveryDialogResult
                {
                    Items = requestedItems,
                    Notes = dialogResult!.Notes
                };
                await ConfirmPickupAsync(refreshedPickup);
            }
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task PrintDeliveryNoteAsync(DeliveryReceiptClientDto receipt)
    {
        try
        {
            var bytes = await _salesService.GetDeliveryNoteAsync(receipt.SaleId, receipt.DeliveryId);
            if (bytes.Length == 0)
            {
                _dialogService.ShowWarning("Nota de Despacho", "La nota de despacho no está disponible.");
                return;
            }

            _openDeliveryNote(receipt, bytes);
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Nota de Despacho", $"No se pudo abrir la nota de despacho: {ex.Message}");
        }
    }

    private static void SaveAndOpenDeliveryNote(DeliveryReceiptClientDto receipt, byte[] bytes)
    {
        var directory = Path.Combine(Path.GetTempPath(), "CommandCenterDeliveryNotes");
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(
            directory,
            $"Nota_Despacho_{receipt.SaleId}_{receipt.DeliveryId}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
        File.WriteAllBytes(filePath, bytes);
        Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
    }
}
