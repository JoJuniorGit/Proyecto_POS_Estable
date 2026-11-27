using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class PendingPickupsViewModel : ObservableObject, IDisposable
{
    private readonly ISalesService _salesService;
    private readonly IDialogService _dialogService;
    private readonly Action<DeliveryReceiptClientDto, byte[]>? _openDeliveryNote;

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

    [ObservableProperty]
    private PendingPickupClientDto? _selectedCustody;

    [ObservableProperty]
    private PickupStatusFilter _selectedStatusFilter = PickupStatusFilter.All;

    [ObservableProperty]
    private PickupSortOption _selectedSortOption;

    public event Action<string>? RequestClipboardCopy;

    private const int PageSize = 200;
    private int _totalCount;
    private bool _loaded;
    private int _allMatchCount;
    private int _notPickedUpMatchCount;
    private int _partialMatchCount;

    public ObservableCollection<PendingPickupClientDto> Pickups { get; } = new();

    public ObservableCollection<PendingPickupClientDto> FilteredPickups { get; } = new();

    public IReadOnlyList<PickupSortOption> SortOptions { get; }

    public bool MatchesSearch(PendingPickupClientDto? p)
    {
        if (p == null) return false;
        if (string.IsNullOrWhiteSpace(SearchQuery)) return true;
        var q = SearchQuery.Trim();
        return (p.CustomerName ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase) ||
               (p.CustomerCedula ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase) ||
               (p.InvoiceNumber?.ToString(CultureInfo.InvariantCulture) ?? p.SaleId.ToString(CultureInfo.InvariantCulture))
                   .Contains(q, StringComparison.OrdinalIgnoreCase) ||
               (p.Items ?? Enumerable.Empty<PendingPickupItemDto>())
                   .Any(item => item is not null
                       && (item.ProductName ?? string.Empty).Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    public PendingPickupsViewModel(
        ISalesService salesService,
        IDialogService dialogService,
        Action<DeliveryReceiptClientDto, byte[]>? openDeliveryNote = null)
    {
        _salesService = salesService;
        _dialogService = dialogService;
        _openDeliveryNote = openDeliveryNote;

        SortOptions = new[]
        {
            new PickupSortOption("Más recientes primero", PickupSortMode.NewestFirst),
            new PickupSortOption("Más antiguas primero", PickupSortMode.OldestFirst),
            new PickupSortOption("Mayor cantidad pendiente", PickupSortMode.MostPendingUnits)
        };
        _selectedSortOption = SortOptions[0];
    }

    // ── KPIs (full loaded collection; A4) ──

    public int ActiveCustodyCount => Pickups.Count;

    public int DistinctCustomersCount => Pickups
        .Select(CustomerKey)
        .Where(key => key.Length > 0)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Count();

    public string ActiveCustodySubtitle => DistinctCustomersCount == 1
        ? "1 cliente con retiros abiertos"
        : $"{DistinctCustomersCount} clientes con retiros abiertos";

    public decimal UnitsToDeliver => Pickups.Sum(p => p.PendingUnits);

    public string UnitsToDeliverSubtitle
    {
        get
        {
            int productLines = Pickups.Sum(p => p.PendingLineCount);
            return productLines == 1
                ? "1 línea de producto por despachar"
                : $"{productLines} líneas de producto por despachar";
        }
    }

    public int PartialPickupsCount => Pickups.Count(p => p.DeliveredUnits > 0m && p.PendingUnits > 0m);

    public string PartialPickupsValue => PartialPickupsCount == 0
        ? "0 parciales"
        : PartialPickupsCount.ToString(CultureInfo.InvariantCulture);

    public string PartialPickupsSubtitle => PartialPickupsCount == 0
        ? "Sin entregas parciales registradas"
        : "Facturas con retiro parcial en curso";

    public decimal CustodyValueBsS => Pickups.Sum(p => p.PendingValueBsS);

    public decimal CustodyValueUSD => Pickups.Sum(p => p.PendingValueUSD);

    // ── Filter labels and empty state ──

    public string AllFilterLabel => $"Todas ({_allMatchCount})";

    public string NotPickedUpFilterLabel => $"Sin retirar ({_notPickedUpMatchCount})";

    public string PartialFilterLabel => $"Parciales ({_partialMatchCount})";

    public string VisiblePickupCountLabel => FilteredPickups.Count == 1
        ? "1 mostrada"
        : $"{FilteredPickups.Count} mostradas";

    public bool HasActiveFilters => !string.IsNullOrWhiteSpace(SearchQuery)
        || SelectedStatusFilter != PickupStatusFilter.All;

    public bool IsListEmpty => FilteredPickups.Count == 0;

    public string EmptyStateTitle => Pickups.Count == 0
        ? "No hay retiros pendientes"
        : "Sin resultados para tu búsqueda";

    public string EmptyStateDescription => Pickups.Count == 0
        ? "Las facturas pagadas pendientes de entrega física aparecerán aquí."
        : "Prueba con otro número de factura, cliente, cédula/RIF o producto.";

    public void RefreshFilteredPickups()
    {
        var matched = Pickups
            .Where(p => p is not null && MatchesSearch(p))
            .ToList();

        _allMatchCount = matched.Count;
        _notPickedUpMatchCount = matched.Count(p => p.DeliveredUnits == 0m);
        _partialMatchCount = matched.Count(p => p.DeliveredUnits > 0m && p.PendingUnits > 0m);

        IEnumerable<PendingPickupClientDto> visible = SelectedStatusFilter switch
        {
            PickupStatusFilter.NotPickedUp => matched.Where(p => p.DeliveredUnits == 0m),
            PickupStatusFilter.Partial => matched.Where(p => p.DeliveredUnits > 0m && p.PendingUnits > 0m),
            _ => matched
        };

        visible = SelectedSortOption.Mode switch
        {
            PickupSortMode.OldestFirst => visible.OrderBy(p => p.Date),
            PickupSortMode.MostPendingUnits => visible
                .OrderByDescending(p => p.PendingUnits)
                .ThenByDescending(p => p.Date),
            _ => visible.OrderByDescending(p => p.Date)
        };

        var visibleList = visible.ToList();

        FilteredPickups.Clear();
        foreach (var pickup in visibleList)
        {
            FilteredPickups.Add(pickup);
        }

        if (SelectedCustody is null || !visibleList.Contains(SelectedCustody))
        {
            SelectedCustody = visibleList.FirstOrDefault();
        }

        NotifyPresentationChanged();
    }

    private void NotifyPresentationChanged()
    {
        OnPropertyChanged(nameof(FilteredPickups));
        OnPropertyChanged(nameof(ActiveCustodyCount));
        OnPropertyChanged(nameof(DistinctCustomersCount));
        OnPropertyChanged(nameof(ActiveCustodySubtitle));
        OnPropertyChanged(nameof(UnitsToDeliver));
        OnPropertyChanged(nameof(UnitsToDeliverSubtitle));
        OnPropertyChanged(nameof(PartialPickupsCount));
        OnPropertyChanged(nameof(PartialPickupsValue));
        OnPropertyChanged(nameof(PartialPickupsSubtitle));
        OnPropertyChanged(nameof(CustodyValueBsS));
        OnPropertyChanged(nameof(CustodyValueUSD));
        OnPropertyChanged(nameof(AllFilterLabel));
        OnPropertyChanged(nameof(NotPickedUpFilterLabel));
        OnPropertyChanged(nameof(PartialFilterLabel));
        OnPropertyChanged(nameof(VisiblePickupCountLabel));
        OnPropertyChanged(nameof(HasActiveFilters));
        OnPropertyChanged(nameof(IsListEmpty));
        OnPropertyChanged(nameof(EmptyStateTitle));
        OnPropertyChanged(nameof(EmptyStateDescription));
    }

    private static string CustomerKey(PendingPickupClientDto pickup)
    {
        var cedula = pickup.CustomerCedula?.Trim();
        if (!string.IsNullOrEmpty(cedula)) return $"C:{cedula}";
        var name = pickup.CustomerName?.Trim();
        return string.IsNullOrEmpty(name) ? string.Empty : $"N:{name}";
    }

    partial void OnSearchQueryChanged(string value) => RefreshFilteredPickups();

    partial void OnSelectedStatusFilterChanged(PickupStatusFilter value) => RefreshFilteredPickups();

    partial void OnSelectedSortOptionChanged(PickupSortOption value) => RefreshFilteredPickups();

    [RelayCommand]
    private void ClearSearch() => SearchQuery = string.Empty;

    [RelayCommand]
    private void ClearFilters()
    {
        SearchQuery = string.Empty;
        SelectedStatusFilter = PickupStatusFilter.All;
        SelectedSortOption = SortOptions[0];
        RefreshFilteredPickups();
    }

    [RelayCommand]
    private void SetStatusFilter(PickupStatusFilter filter) => SelectedStatusFilter = filter;

    [RelayCommand]
    private void CopyCustomerPhone()
    {
        var phone = SelectedCustody?.CustomerPhone;
        if (!string.IsNullOrWhiteSpace(phone))
        {
            RequestClipboardCopy?.Invoke(phone!);
        }
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
            RefreshFilteredPickups();
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
            RefreshFilteredPickups();
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
    private async Task DeliverAllAsync(PendingPickupClientDto? pickup)
    {
        if (pickup == null) return;

        pickup.PendingDraft = new PartialDeliveryDialogResult
        {
            Items = pickup.Items
                .Where(item => item.PendingQuantity > 0m)
                .Select(item => new PartialDeliveryItemRequestDto
                {
                    SaleItemId = item.SaleItemId,
                    Quantity = item.PendingQuantity
                })
                .ToList()
        };

        await ConfirmPickupAsync(pickup);
    }

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
                    RefreshFilteredPickups();
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

            if (_openDeliveryNote != null)
            {
                _openDeliveryNote(receipt, bytes);
            }
            else
            {
                await SaveAndOpenDeliveryNoteAsync(receipt, bytes);
            }
        }
        catch (Exception ex)
        {
            _dialogService.ShowError("Nota de Despacho", $"No se pudo abrir la nota de despacho: {ex.Message}");
        }
    }

    private static async Task SaveAndOpenDeliveryNoteAsync(DeliveryReceiptClientDto receipt, byte[] bytes)
    {
        var directory = Path.Combine(Path.GetTempPath(), "CommandCenterDeliveryNotes");
        Directory.CreateDirectory(directory);
        var filePath = Path.Combine(
            directory,
            $"Nota_Despacho_{receipt.SaleId}_{receipt.DeliveryId}_{DateTime.Now:yyyyMMdd_HHmmss}.pdf");
        await File.WriteAllBytesAsync(filePath, bytes);
        Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
    }

    private bool _disposed;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing)
        {
            Pickups.Clear();
            FilteredPickups.Clear();
        }
        _disposed = true;
    }
}

public enum PickupStatusFilter
{
    All,
    NotPickedUp,
    Partial
}

public enum PickupSortMode
{
    NewestFirst,
    OldestFirst,
    MostPendingUnits
}

public sealed record PickupSortOption(string Label, PickupSortMode Mode);
