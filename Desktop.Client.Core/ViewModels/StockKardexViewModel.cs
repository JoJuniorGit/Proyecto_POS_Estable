using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Core.Common;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Core.Logging;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

/// <summary>
/// 8.177-T3 (S2/S3): Centro de Auditoría de Inventario (Kardex) en Desktop. Filtros combinables
/// con atajos de fecha local→UTC, búsqueda predictiva de producto, KPIs, grilla paginada
/// server-side y exportación CSV/XLSX/PDF. Solo lectura y solo Admin/Manager (RBAC del
/// <c>StockMovementsController</c> de T2).
/// </summary>
public partial class StockKardexViewModel : ObservableObject, IDisposable
{
    public const int FixedPageSize = 25;
    public const string AccessDeniedMessage = "Su rol no tiene acceso al Centro de Auditoría de Inventario (Kardex).";

    private readonly IStockKardexClientService _kardexService;
    private readonly IProductService _productService;
    private readonly IDialogService? _dialogService;
    private readonly IFilePickerDialog _filePicker;
    private readonly IDispatcherInvoker _dispatcherInvoker;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _suggestionCts;
    private bool _hasLoaded;
    private bool _isSelectingProduct;
    private int _disposed;

    public UserSession? UserSession { get; }

    /// <summary>RBAC de lectura del Kardex: Admin/Manager; sin sesión (tests headless) se permite.</summary>
    public bool HasAccess => UserSession == null || UserSession.IsAdmin || UserSession.IsManager;

    public bool CanExport => HasAccess && !IsBusy && !IsExporting;

    public ObservableCollection<StockKardexRowViewModel> Rows { get; } = new();
    public ObservableCollection<ProductQuickInfoDto> ProductSuggestions { get; } = new();
    public IReadOnlyList<StockMovementReasonOption> ReasonOptions { get; }
    public IReadOnlyList<StockMovementOriginOption> OriginOptions { get; }
    public IReadOnlyList<StockKardexDirectionOption> DirectionOptions { get; }

    // ── Filtros ─────────────────────────────────────────────────────────────
    [ObservableProperty] private DateTime? _dateFrom;
    [ObservableProperty] private DateTime? _dateTo;
    [ObservableProperty] private string _productSearchText = string.Empty;
    [ObservableProperty] private ProductQuickInfoDto? _selectedProduct;
    [ObservableProperty] private bool _isProductSuggestionsOpen;
    [ObservableProperty] private StockMovementReasonOption? _selectedReason;
    [ObservableProperty] private StockMovementOriginOption? _selectedOrigin;
    [ObservableProperty] private StockKardexDirectionOption? _selectedDirection;

    // ── Estado ──────────────────────────────────────────────────────────────
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private int _currentPage = 1;
    [ObservableProperty] private int _totalCount;

    // ── KPIs (valores crudos + displays deterministas) ──────────────────────
    [ObservableProperty] private string _totalMovementsDisplay = "0";
    [ObservableProperty] private string _netVolumeDisplay = "0";
    [ObservableProperty] private string _entriesValueDisplay = "$0,00";
    [ObservableProperty] private string _lossesValueDisplay = "$0,00";
    [ObservableProperty] private string _netValueDisplay = "$0,00";
    [ObservableProperty] private long _unvaluedMovements;

    public StockKardexViewModel(
        IStockKardexClientService kardexService,
        IProductService productService,
        IDialogService? dialogService = null,
        IFilePickerDialog? filePicker = null,
        UserSession? userSession = null,
        IDispatcherInvoker? dispatcherInvoker = null)
    {
        ArgumentNullException.ThrowIfNull(kardexService);
        ArgumentNullException.ThrowIfNull(productService);

        _kardexService = kardexService;
        _productService = productService;
        _dialogService = dialogService;
        _filePicker = filePicker ?? new NoopFilePicker();
        _dispatcherInvoker = dispatcherInvoker ?? new InlineDispatcherInvoker();
        UserSession = userSession;

        ReasonOptions = StockMovementLabels.CreateReasonOptions();
        OriginOptions = StockMovementLabels.CreateOriginOptions();
        DirectionOptions = StockMovementLabels.CreateDirectionOptions();
        _selectedReason = ReasonOptions[0];
        _selectedOrigin = OriginOptions[0];
        _selectedDirection = DirectionOptions[0];

        // Default consistente con el historial de ventas: la jornada local de hoy.
        var today = TimeZoneHelper.GetVenezuelaDate().ToDateTime(TimeOnly.MinValue);
        _dateFrom = today;
        _dateTo = today;
    }

    public int TotalPages => TotalCount > 0 ? (int)Math.Ceiling((double)TotalCount / FixedPageSize) : 1;

    public bool CanGoFirst => CurrentPage > 1 && TotalPages > 1;
    public bool CanGoPrevious => CurrentPage > 1 && TotalPages > 1;
    public bool CanGoNext => CurrentPage < TotalPages && TotalPages > 1;
    public bool CanGoLast => CurrentPage < TotalPages && TotalPages > 1;

    public string PageSummary => TotalCount <= 0
        ? "Página 0 de 0 (0 movimientos)"
        : $"Página {CurrentPage} de {TotalPages} ({TotalCount} movimientos)";

    public bool HasUnvaluedMovements => UnvaluedMovements > 0;

    public string UnvaluedWarningText => UnvaluedMovements > 0
        ? $"{UnvaluedMovements} movimientos sin costo histórico, excluidos de la valorización"
        : string.Empty;

    partial void OnCurrentPageChanged(int value) => NotifyPaginationChanged();
    partial void OnTotalCountChanged(int value) => NotifyPaginationChanged();
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanExport));
    partial void OnIsExportingChanged(bool value) => OnPropertyChanged(nameof(CanExport));

    partial void OnUnvaluedMovementsChanged(long value)
    {
        OnPropertyChanged(nameof(HasUnvaluedMovements));
        OnPropertyChanged(nameof(UnvaluedWarningText));
    }

    private void NotifyPaginationChanged()
    {
        OnPropertyChanged(nameof(TotalPages));
        OnPropertyChanged(nameof(PageSummary));
        OnPropertyChanged(nameof(CanGoFirst));
        OnPropertyChanged(nameof(CanGoPrevious));
        OnPropertyChanged(nameof(CanGoNext));
        OnPropertyChanged(nameof(CanGoLast));
    }

    /// <summary>Carga diferida al navegar a la vista (una sola vez por sesión de vista).</summary>
    public async Task EnsureLoadedAsync()
    {
        if (_hasLoaded)
        {
            return;
        }

        _hasLoaded = true;
        await LoadAsync(reloadSummary: true);
    }

    [RelayCommand]
    private Task RefreshAsync() => LoadAsync(reloadSummary: true);

    // ── Atajos de fecha (día local → instante UTC) ──────────────────────────
    [RelayCommand]
    private Task ApplyTodayAsync()
    {
        var today = TimeZoneHelper.GetVenezuelaDate();
        SetDateRange(today, today);
        return ApplyFiltersAsync();
    }

    [RelayCommand]
    private Task ApplyThisWeekAsync()
    {
        var today = TimeZoneHelper.GetVenezuelaDate();
        var mondayOffset = ((int)today.DayOfWeek + 6) % 7; // lunes = inicio de semana
        SetDateRange(today.AddDays(-mondayOffset), today);
        return ApplyFiltersAsync();
    }

    [RelayCommand]
    private Task ApplyCurrentMonthAsync()
    {
        var today = TimeZoneHelper.GetVenezuelaDate();
        SetDateRange(new DateOnly(today.Year, today.Month, 1), today);
        return ApplyFiltersAsync();
    }

    [RelayCommand]
    private Task ApplyFiltersAsync()
    {
        CurrentPage = 1;
        return LoadAsync(reloadSummary: true);
    }

    [RelayCommand]
    private Task ClearFiltersAsync()
    {
        var today = TimeZoneHelper.GetVenezuelaDate();
        SetDateRange(today, today);
        ResetProductSelection();
        SelectedReason = ReasonOptions[0];
        SelectedOrigin = OriginOptions[0];
        SelectedDirection = DirectionOptions[0];
        return ApplyFiltersAsync();
    }

    private void SetDateRange(DateOnly from, DateOnly to)
    {
        DateFrom = from.ToDateTime(TimeOnly.MinValue);
        DateTo = to.ToDateTime(TimeOnly.MinValue);
    }

    private void ResetProductSelection()
    {
        // 8.177-T3 (fix verificación): cancela una búsqueda de sugerencias en vuelo para que no
        // reaparezca el popup tras limpiar la selección (mismo patrón que SearchProductsAsync).
        var pendingSearch = Interlocked.Exchange(ref _suggestionCts, null);
        try
        {
            pendingSearch?.Cancel();
            pendingSearch?.Dispose();
        }
        catch (ObjectDisposedException) { }

        _isSelectingProduct = true;
        try
        {
            ProductSearchText = string.Empty;
        }
        finally
        {
            _isSelectingProduct = false;
        }

        SelectedProduct = null;
        ProductSuggestions.Clear();
        IsProductSuggestionsOpen = false;
    }

    // ── Búsqueda predictiva de producto ─────────────────────────────────────
    partial void OnProductSearchTextChanged(string value)
    {
        if (_isSelectingProduct)
        {
            return;
        }

        if (SelectedProduct != null)
        {
            SelectedProduct = null;
        }

        SearchProductsAsync(value).SafeFireAndForget("StockKardexViewModel.SearchProducts");
    }

    partial void OnSelectedProductChanged(ProductQuickInfoDto? value)
    {
        if (value == null)
        {
            return;
        }

        _isSelectingProduct = true;
        try
        {
            ProductSearchText = $"{value.SKU} — {value.Name}";
        }
        finally
        {
            _isSelectingProduct = false;
        }

        IsProductSuggestionsOpen = false;
    }

    private async Task SearchProductsAsync(string? term)
    {
        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _suggestionCts, newCts);
        try
        {
            oldCts?.Cancel();
            oldCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        var token = newCts.Token;
        if (string.IsNullOrWhiteSpace(term))
        {
            ProductSuggestions.Clear();
            IsProductSuggestionsOpen = false;
            return;
        }

        try
        {
            await Task.Delay(300, token);
            var results = await _productService.GetSuggestionsAsync(term.Trim(), activeOnly: true, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            var valid = results.Where(suggestion => suggestion.Id > 0).ToList();
            void Apply()
            {
                ProductSuggestions.Clear();
                foreach (var suggestion in valid)
                {
                    ProductSuggestions.Add(suggestion);
                }

                IsProductSuggestionsOpen = valid.Count > 0;
            }

            if (!_dispatcherInvoker.CheckAccess())
            {
                _dispatcherInvoker.Invoke(Apply);
            }
            else
            {
                Apply();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ClientStateLogger.LogError($"Fallo al buscar productos en el Kardex: {ex.Message}", nameof(StockKardexViewModel));
        }
    }

    // ── Paginación ──────────────────────────────────────────────────────────
    [RelayCommand]
    private Task FirstPageAsync() => CanGoFirst ? NavigateToPageAsync(1) : Task.CompletedTask;

    [RelayCommand]
    private Task PreviousPageAsync() => CanGoPrevious ? NavigateToPageAsync(CurrentPage - 1) : Task.CompletedTask;

    [RelayCommand]
    private Task NextPageAsync() => CanGoNext ? NavigateToPageAsync(CurrentPage + 1) : Task.CompletedTask;

    [RelayCommand]
    private Task LastPageAsync() => CanGoLast ? NavigateToPageAsync(TotalPages) : Task.CompletedTask;

    private Task NavigateToPageAsync(int page)
    {
        CurrentPage = page;
        return LoadAsync(reloadSummary: false);
    }

    // ── Exportación ─────────────────────────────────────────────────────────
    [RelayCommand]
    private Task ExportCsvAsync() => ExportAsync("csv", "csv", "CSV (*.csv)|*.csv");

    [RelayCommand]
    private Task ExportExcelAsync() => ExportAsync("xlsx", "xlsx", "Excel (*.xlsx)|*.xlsx");

    [RelayCommand]
    private Task ExportPdfAsync() => ExportAsync("pdf", "pdf", "PDF (*.pdf)|*.pdf");

    private async Task ExportAsync(string format, string extension, string pickerFilter)
    {
        if (!CanExport)
        {
            return;
        }

        IsExporting = true;
        try
        {
            var bytes = await _kardexService.ExportAsync(BuildFilter(), format, CancellationToken.None);
            var path = _filePicker.PickSaveFilePath(
                "Exportar Kardex",
                pickerFilter,
                $"kardex-{DateTime.Now:yyyyMMdd-HHmmss}.{extension}");
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            await File.WriteAllBytesAsync(path, bytes);
            _dialogService?.ShowInfo("Exportación completada", $"El reporte del Kardex se guardó correctamente en:\n{path}");
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogError($"Fallo al exportar el Kardex ({format}): {ex.Message}", nameof(StockKardexViewModel));
            ErrorMessage = $"Error al exportar el Kardex: {ex.Message}";
            _dialogService?.ShowError("Error de Exportación", $"No se pudo exportar el Kardex: {ex.Message}");
        }
        finally
        {
            IsExporting = false;
        }
    }

    // ── Carga y filtro ──────────────────────────────────────────────────────
    private StockKardexFilterDto BuildFilter()
    {
        var (startUtc, endExclusiveUtc) = TimeZoneHelper.GetUtcRange(DateFrom, DateTo);
        return new StockKardexFilterDto
        {
            DateFromUtc = startUtc,
            // T2 aplica `<=` sobre DateToUtc; el fin de jornada local viaja como su último tick.
            DateToUtc = endExclusiveUtc.HasValue ? endExclusiveUtc.Value.AddTicks(-1) : null,
            ProductId = SelectedProduct?.Id,
            ReasonCodes = SelectedReason?.Value is { } reason ? new[] { reason } : null,
            OriginTypes = SelectedOrigin?.Value is { } origin ? new[] { origin } : null,
            Direction = SelectedDirection?.Value ?? StockKardexDirection.All
        };
    }

    private async Task LoadAsync(bool reloadSummary)
    {
        if (!HasAccess)
        {
            ErrorMessage = AccessDeniedMessage;
            return;
        }

        var newCts = new CancellationTokenSource();
        var oldCts = Interlocked.Exchange(ref _loadCts, newCts);
        try
        {
            oldCts?.Cancel();
            oldCts?.Dispose();
        }
        catch (ObjectDisposedException) { }

        var token = newCts.Token;
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            var filter = BuildFilter();
            var movementsTask = _kardexService.GetMovementsAsync(filter, CurrentPage, FixedPageSize, token);
            var summaryTask = reloadSummary ? _kardexService.GetSummaryAsync(filter, token) : null;

            if (summaryTask != null)
            {
                await Task.WhenAll(movementsTask, summaryTask);
            }
            else
            {
                await movementsTask;
            }

            if (token.IsCancellationRequested)
            {
                return;
            }

            var page = await movementsTask;
            var summary = summaryTask != null ? await summaryTask : null;
            var rows = page.Items.Select(item => new StockKardexRowViewModel(item)).ToList();

            void Apply()
            {
                TotalCount = page.TotalCount;
                Rows.Clear();
                foreach (var row in rows)
                {
                    Rows.Add(row);
                }

                if (summary != null)
                {
                    ApplySummary(summary);
                }
            }

            if (!_dispatcherInvoker.CheckAccess())
            {
                _dispatcherInvoker.Invoke(Apply);
            }
            else
            {
                Apply();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            ClientStateLogger.LogError($"Fallo al cargar el Kardex: {ex.Message}", nameof(StockKardexViewModel));
            ErrorMessage = $"Error al cargar el Kardex: {ex.Message}";
        }
        finally
        {
            if (!token.IsCancellationRequested)
            {
                IsBusy = false;
            }
        }
    }

    private void ApplySummary(StockKardexSummaryDto summary)
    {
        TotalMovementsDisplay = summary.TotalMovements.ToString("N0", MoneyFormat.Culture);
        NetVolumeDisplay = summary.NetVolume.ToString("#,##0.###", MoneyFormat.Culture);
        EntriesValueDisplay = $"${MoneyFormat.N2(summary.EntriesValueUsd)}";
        LossesValueDisplay = $"${MoneyFormat.N2(summary.LossesValueUsd)}";
        NetValueDisplay = $"${MoneyFormat.N2(summary.NetValueUsd)}";
        UnvaluedMovements = summary.UnvaluedMovements;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var cts in new[] { _loadCts, _suggestionCts })
        {
            try
            {
                cts?.Cancel();
                cts?.Dispose();
            }
            catch (ObjectDisposedException) { }
        }

        _loadCts = null;
        _suggestionCts = null;
        GC.SuppressFinalize(this);
    }
}

/// <summary>Opción de filtro por motivo estandarizado; <see cref="Value"/> null = "Todos".</summary>
public sealed record StockMovementReasonOption(StockMovementReason? Value, string Label);

/// <summary>Opción de filtro por tipo de origen; <see cref="Value"/> null = "Todos".</summary>
public sealed record StockMovementOriginOption(StockMovementOriginType? Value, string Label);

/// <summary>Opción de filtro por sentido del movimiento.</summary>
public sealed record StockKardexDirectionOption(StockKardexDirection Value, string Label);

/// <summary>Etiquetas en español de la clasificación del Kardex (UI Desktop y diálogo de ajuste).</summary>
public static class StockMovementLabels
{
    public static string Reason(StockMovementReason? reason) => reason switch
    {
        StockMovementReason.Sale => "Venta",
        StockMovementReason.PurchaseEntry => "Entrada por Compra",
        StockMovementReason.Damage => "Merma/Daño",
        StockMovementReason.Expiry => "Vencimiento",
        StockMovementReason.PhysicalCountCorrection => "Corrección de Conteo Físico",
        StockMovementReason.Return => "Devolución",
        StockMovementReason.InitialLoad => "Carga inicial",
        StockMovementReason.ImportAdjustment => "Ajuste de Importación",
        _ => "Sin clasificar"
    };

    public static string Origin(StockMovementOriginType? origin) => origin switch
    {
        StockMovementOriginType.Manual => "Manual",
        StockMovementOriginType.Import => "Importación",
        StockMovementOriginType.System => "Sistema",
        _ => "Sin clasificar"
    };

    public static IReadOnlyList<StockMovementReasonOption> CreateReasonOptions() => new[]
    {
        new StockMovementReasonOption(null, "Todos"),
        new StockMovementReasonOption(StockMovementReason.Sale, Reason(StockMovementReason.Sale)),
        new StockMovementReasonOption(StockMovementReason.PurchaseEntry, Reason(StockMovementReason.PurchaseEntry)),
        new StockMovementReasonOption(StockMovementReason.Damage, Reason(StockMovementReason.Damage)),
        new StockMovementReasonOption(StockMovementReason.Expiry, Reason(StockMovementReason.Expiry)),
        new StockMovementReasonOption(StockMovementReason.PhysicalCountCorrection, Reason(StockMovementReason.PhysicalCountCorrection)),
        new StockMovementReasonOption(StockMovementReason.Return, Reason(StockMovementReason.Return)),
        new StockMovementReasonOption(StockMovementReason.InitialLoad, Reason(StockMovementReason.InitialLoad)),
        new StockMovementReasonOption(StockMovementReason.ImportAdjustment, Reason(StockMovementReason.ImportAdjustment))
    };

    public static IReadOnlyList<StockMovementOriginOption> CreateOriginOptions() => new[]
    {
        new StockMovementOriginOption(null, "Todos"),
        new StockMovementOriginOption(StockMovementOriginType.Manual, Origin(StockMovementOriginType.Manual)),
        new StockMovementOriginOption(StockMovementOriginType.Import, Origin(StockMovementOriginType.Import)),
        new StockMovementOriginOption(StockMovementOriginType.System, Origin(StockMovementOriginType.System))
    };

    public static IReadOnlyList<StockKardexDirectionOption> CreateDirectionOptions() => new[]
    {
        new StockKardexDirectionOption(StockKardexDirection.All, "Todos"),
        new StockKardexDirectionOption(StockKardexDirection.In, "Entradas"),
        new StockKardexDirectionOption(StockKardexDirection.Out, "Salidas")
    };
}

/// <summary>Fila de la grilla del Kardex con los displays de auditoría (fecha local + UTC en tooltip).</summary>
public sealed class StockKardexRowViewModel
{
    private readonly StockKardexRowDto _dto;

    public StockKardexRowViewModel(StockKardexRowDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        _dto = dto;
    }

    public string DateLocalText => TimeZoneHelper.ToVenezuelaTime(_dto.MovementDateUtc)
        .ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.CurrentCulture);

    public string DateUtcText => _dto.MovementDateUtc.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

    public string Sku => _dto.SKU;
    public string ProductName => _dto.ProductName;
    public string ProductDisplay => $"{_dto.SKU} — {_dto.ProductName}";

        public bool IsEntry => _dto.QuantityChange > 0;

    public string DeltaText => (_dto.QuantityChange > 0 ? "+" : string.Empty)
        + _dto.QuantityChange.ToString("#,##0.###", MoneyFormat.Culture);

    public string StockBeforeText => _dto.StockBefore.ToString("#,##0.###", MoneyFormat.Culture);
    public string NewStockText => _dto.NewStockLevel.ToString("#,##0.###", MoneyFormat.Culture);

    public string UnitCostDisplay => _dto.UnitCostUSD.HasValue ? $"${MoneyFormat.N2(_dto.UnitCostUSD.Value)}" : "—";
    public string TotalValueDisplay => _dto.TotalValueUSD.HasValue ? $"${MoneyFormat.N2(_dto.TotalValueUSD.Value)}" : "—";

    public string OriginLabel => StockMovementLabels.Origin(_dto.OriginType);
    public string ReasonLabel => StockMovementLabels.Reason(_dto.ReasonCode);
    public string ReasonTooltip => string.IsNullOrWhiteSpace(_dto.Reason) ? ReasonLabel : _dto.Reason;

    public string ExecutedByDisplay => DisplayOrDash(_dto.ExecutedByUserId);
    public string AuthorizedByDisplay => DisplayOrDash(_dto.AuthorizedByUserId);

    // Sin acción clicable en T3: navegar al documento origen exige tocar los ViewModels de
    // facturas (WIP ajeno). Follow-up: enlace directo cuando ese WIP cierre.
    public string ReferenceDisplay => _dto.SaleId.HasValue
        ? $"#venta {_dto.SaleId.Value}"
        : _dto.SupplierInvoiceId.HasValue
            ? $"#factura {_dto.SupplierInvoiceId.Value}"
            : "—";

    public string ObservationDisplay => DisplayOrDash(_dto.Observation);

    private static string DisplayOrDash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
}
