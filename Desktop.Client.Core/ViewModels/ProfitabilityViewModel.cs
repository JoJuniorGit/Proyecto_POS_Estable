using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;
using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

/// <summary>F4-WU1 (Paso 14): conmutador de las series y tablas del dashboard.</summary>
public enum ProfitabilityViewMode
{
    Accrued,
    Realized,
    Both
}

/// <summary>
/// F4-WU1 (Paso 14): Estado de Resultados del cliente WPF. La vista es de solo lectura sobre la
/// rentabilidad (las comisiones son costo del negocio y nunca se suman al cliente), y el acceso
/// queda restringido a Admin/Manager igual que el endpoint F3.
/// </summary>
public partial class ProfitabilityViewModel : ObservableObject, IDisposable
{
    private readonly IProfitabilityClientService _profitabilityService;
    private readonly IFilePickerDialog _filePicker;
    private readonly IDialogService? _dialogService;
    private CancellationTokenSource? _loadCts;
    private int _disposed;
    private bool _hasLoaded;
    private ProfitabilitySummaryClientDto? _summary;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _errorMessage = string.Empty;

    [ObservableProperty]
    private bool _isExporting;

    [ObservableProperty]
    private string _exportStatus = string.Empty;

    [ObservableProperty]
    private ProfitabilityPeriodPresetClient _selectedPreset = ProfitabilityPeriodPresetClient.Today;

    [ObservableProperty]
    private DateTime? _customFromDate = DateTime.Today;

    [ObservableProperty]
    private DateTime? _customToDate = DateTime.Today;

    [ObservableProperty]
    private ProfitabilityGroupByClient _selectedGroupBy = ProfitabilityGroupByClient.Day;

    [ObservableProperty]
    private ProfitabilityViewMode _viewMode = ProfitabilityViewMode.Both;

    public ProfitabilityViewModel(
        IProfitabilityClientService profitabilityService,
        IFilePickerDialog? filePicker = null,
        IDialogService? dialogService = null,
        UserSession? userSession = null)
    {
        _profitabilityService = profitabilityService
            ?? throw new ArgumentNullException(nameof(profitabilityService));
        _filePicker = filePicker ?? new NoopFilePicker();
        _dialogService = dialogService;
        UserSession = userSession;
    }

    public UserSession? UserSession { get; }

    public ObservableCollection<ProfitabilitySeriesBarViewModel> SeriesBars { get; } = new();

    public ObservableCollection<ProfitabilityPaymentMethodRowViewModel> PaymentMethodRows { get; } = new();

    public ObservableCollection<ProfitabilityProductRowViewModel> ProductRows { get; } = new();

    public ObservableCollection<ProfitabilityCategoryRowViewModel> CategoryRows { get; } = new();

    public ObservableCollection<ProfitabilitySellerRowViewModel> SellerRows { get; } = new();

    public ObservableCollection<ProfitabilityAlertRowViewModel> Alerts { get; } = new();

    public bool CanViewProfitability =>
        UserSession == null || UserSession.IsAdmin || UserSession.IsManager;

    public bool HasData => _summary != null;

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    public bool IsCustomRange => SelectedPreset == ProfitabilityPeriodPresetClient.Custom;

    public bool CanExport => CanViewProfitability && HasData && !IsExporting;

    public bool HasCoverageWarnings =>
        (_summary?.Coverage.UnknownCostRevenue ?? 0m) > 0m
        || (_summary?.Coverage.UnknownCostLinesCount ?? 0) > 0
        || (_summary?.Coverage.UnknownFeePaymentAmount ?? 0m) > 0m
        || (_summary?.Coverage.UnknownFeePaymentsCount ?? 0) > 0;

    public bool IsEmpty =>
        !IsLoading
        && HasData
        && SeriesBars.Count == 0
        && PaymentMethodRows.Count == 0
        && ProductRows.Count == 0
        && CategoryRows.Count == 0
        && SellerRows.Count == 0
        && Alerts.Count == 0;

    public ProfitabilitySummaryClientDto? Summary => _summary;

    public decimal AccruedNetProfitValue => _summary?.Accrued.NetProfit ?? 0m;

    public decimal RealizedNetProfitValue => _summary?.Realized.NetProfit ?? 0m;

    public decimal AccruedGrossProfitValue => _summary?.Accrued.GrossProfit ?? 0m;

    public decimal AccruedNetSalesValue => _summary?.Accrued.NetSales ?? 0m;

    public string UtilidadDevengadaDisplay => FormatMoney(AccruedNetProfitValue);

    public string UtilidadPercibidaDisplay => FormatMoney(RealizedNetProfitValue);

    public string VentasNetasDisplay => FormatMoney(AccruedNetSalesValue);

    public string CmvDisplay => FormatMoney(_summary?.Accrued.Cogs ?? 0m);

    public string UtilidadBrutaDisplay => FormatMoney(AccruedGrossProfitValue);

    public string MargenDisplay => FormatPercent(_summary?.Accrued.GrossMarginPercent ?? 0m);

    public string ComisionesServicioDisplay => FormatMoney(_summary?.Accrued.ServiceCommissions ?? 0m);

    public string FxDisplay => FormatMoney(_summary?.Accrued.FxDifferenceInformational ?? 0m);

    public string CajaChicaDisplay => FormatMoney(_summary?.Accrued.PettyCashExpenses ?? 0m);

    public string ComisionesMediosDisplay => FormatMoney(_summary?.Accrued.PaymentMethodFees ?? 0m);

    public string UtilidadNetaDisplay => FormatMoney(AccruedNetProfitValue);

    public string MargenNetoDisplay => FormatPercent(_summary?.Accrued.NetMarginPercent ?? 0m);

    public string CostCoverageDisplay => FormatPercent(_summary?.Coverage.CostCoveragePercent ?? 0m);

    public string FeeCoverageDisplay => FormatPercent(_summary?.Coverage.FeeCoveragePercent ?? 0m);

    public string UnknownCostRevenueDisplay => FormatMoney(_summary?.Coverage.UnknownCostRevenue ?? 0m);

    public string UnknownFeePaymentAmountDisplay => FormatMoney(_summary?.Coverage.UnknownFeePaymentAmount ?? 0m);

    public int UnknownCostLinesCount => _summary?.Coverage.UnknownCostLinesCount ?? 0;

    public int UnknownFeePaymentsCount => _summary?.Coverage.UnknownFeePaymentsCount ?? 0;

    public string GlobalCostsNote => _summary?.GlobalCostsNote ?? string.Empty;

    public bool HasGlobalCostsNote => !string.IsNullOrWhiteSpace(GlobalCostsNote);

    /// <summary>Idempotent entry point used by navigation; Refresh reloads explicitly.</summary>
    public async Task EnsureLoadedAsync()
    {
        if (!CanViewProfitability)
        {
            ErrorMessage = "La rentabilidad está disponible solo para Administradores y Gerentes.";
            return;
        }

        if (_hasLoaded)
        {
            return;
        }

        await LoadAsync();
    }

    public async Task LoadAsync()
    {
        if (!CanViewProfitability)
        {
            ErrorMessage = "La rentabilidad está disponible solo para Administradores y Gerentes.";
            return;
        }

        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _loadCts, cts);
        try
        {
            previous?.Cancel();
            previous?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }

        IsLoading = true;
        ErrorMessage = string.Empty;
        try
        {
            var summary = await _profitabilityService.GetSummaryAsync(BuildFilter(), cts.Token);
            ApplySummary(summary);
            _hasLoaded = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Core.Logging.ClientStateLogger.LogError(
                $"Error al cargar la rentabilidad: {ex.Message}",
                nameof(ProfitabilityViewModel));
            _summary = null;
            ClearCollections();
            ErrorMessage = $"No se pudo cargar la rentabilidad: {ex.Message}";
            NotifySummaryProperties();
        }
        finally
        {
            if (ReferenceEquals(Volatile.Read(ref _loadCts), cts))
            {
                IsLoading = false;
            }
        }
    }

    public ProfitabilityClientFilter BuildFilter()
    {
        var filter = new ProfitabilityClientFilter
        {
            Preset = SelectedPreset,
            GroupBy = SelectedGroupBy
        };

        if (SelectedPreset != ProfitabilityPeriodPresetClient.Custom)
        {
            return filter;
        }

        var from = (CustomFromDate ?? DateTime.Today).Date;
        var to = (CustomToDate ?? DateTime.Today).Date;
        if (to < from)
        {
            (from, to) = (to, from);
        }

        filter.FromDate = DateOnly.FromDateTime(from);
        filter.ToDate = DateOnly.FromDateTime(to);
        return filter;
    }

    [RelayCommand]
    private async Task RefreshAsync() => await LoadAsync();

    [RelayCommand]
    private async Task SelectPresetAsync(string presetName)
    {
        if (!Enum.TryParse<ProfitabilityPeriodPresetClient>(presetName, ignoreCase: true, out var preset))
        {
            return;
        }

        SelectedPreset = preset;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task ApplyCustomRangeAsync()
    {
        SelectedPreset = ProfitabilityPeriodPresetClient.Custom;
        await LoadAsync();
    }

    [RelayCommand]
    private void SetViewMode(ProfitabilityViewMode mode)
    {
        ViewMode = mode;
    }

    partial void OnViewModeChanged(ProfitabilityViewMode value)
    {
        RebuildSeriesBars();
    }

    partial void OnSelectedPresetChanged(ProfitabilityPeriodPresetClient value)
    {
        OnPropertyChanged(nameof(IsCustomRange));
    }

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanExport));
    }

    partial void OnIsExportingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanExport));
    }

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
    }

    private string FormatMoney(decimal value)
    {
        var symbol = _summary?.ReportCurrencySymbol;
        var formatted = CurrencyDisplay.Number(value, 2);
        return string.IsNullOrWhiteSpace(symbol) ? formatted : $"{symbol} {formatted}";
    }

    private static string FormatPercent(decimal value) => $"{CurrencyDisplay.Number(value, 2)} %";

    private void ApplySummary(ProfitabilitySummaryClientDto summary)
    {
        _summary = summary ?? new ProfitabilitySummaryClientDto();
        RebuildBreakdown();
        NotifySummaryProperties();
    }

    private void ClearCollections()
    {
        SeriesBars.Clear();
        PaymentMethodRows.Clear();
        ProductRows.Clear();
        CategoryRows.Clear();
        SellerRows.Clear();
        Alerts.Clear();
    }

    private void NotifySummaryProperties()
    {
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasData));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CanExport));
        OnPropertyChanged(nameof(HasCoverageWarnings));
        OnPropertyChanged(nameof(AccruedNetProfitValue));
        OnPropertyChanged(nameof(RealizedNetProfitValue));
        OnPropertyChanged(nameof(AccruedGrossProfitValue));
        OnPropertyChanged(nameof(AccruedNetSalesValue));
        OnPropertyChanged(nameof(UtilidadDevengadaDisplay));
        OnPropertyChanged(nameof(UtilidadPercibidaDisplay));
        OnPropertyChanged(nameof(VentasNetasDisplay));
        OnPropertyChanged(nameof(CmvDisplay));
        OnPropertyChanged(nameof(UtilidadBrutaDisplay));
        OnPropertyChanged(nameof(MargenDisplay));
        OnPropertyChanged(nameof(ComisionesServicioDisplay));
        OnPropertyChanged(nameof(FxDisplay));
        OnPropertyChanged(nameof(CajaChicaDisplay));
        OnPropertyChanged(nameof(ComisionesMediosDisplay));
        OnPropertyChanged(nameof(UtilidadNetaDisplay));
        OnPropertyChanged(nameof(MargenNetoDisplay));
        OnPropertyChanged(nameof(CostCoverageDisplay));
        OnPropertyChanged(nameof(FeeCoverageDisplay));
        OnPropertyChanged(nameof(UnknownCostRevenueDisplay));
        OnPropertyChanged(nameof(UnknownFeePaymentAmountDisplay));
        OnPropertyChanged(nameof(UnknownCostLinesCount));
        OnPropertyChanged(nameof(UnknownFeePaymentsCount));
        OnPropertyChanged(nameof(GlobalCostsNote));
        OnPropertyChanged(nameof(HasGlobalCostsNote));
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var cts = Interlocked.Exchange(ref _loadCts, null);
        try
        {
            cts?.Cancel();
            cts?.Dispose();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}

/// <summary>F4-WU1: una fila del gráfico temporal devengado vs. percibido.</summary>
public sealed class ProfitabilitySeriesBarViewModel
{
    public string Label { get; init; } = string.Empty;

    public decimal AccruedNetProfit { get; init; }

    public decimal RealizedNetProfit { get; init; }

    public string AccruedDisplay { get; init; } = string.Empty;

    public string RealizedDisplay { get; init; } = string.Empty;

    public double AccruedBarWidth { get; init; }

    public double RealizedBarWidth { get; init; }

    public bool AccruedIsNegative { get; init; }

    public bool RealizedIsNegative { get; init; }

    public bool ShowAccrued { get; init; }

    public bool ShowRealized { get; init; }
}

public sealed class ProfitabilityPaymentMethodRowViewModel
{
    public string Name { get; init; } = string.Empty;

    public string CollectedDisplay { get; init; } = string.Empty;

    public string FeeDisplay { get; init; } = string.Empty;

    public string CoverageDisplay { get; init; } = string.Empty;

    public double BarWidth { get; init; }

    public decimal CollectedAmount { get; init; }

    public decimal FeeAmount { get; init; }
}

public sealed class ProfitabilityProductRowViewModel
{
    public string Name { get; init; } = string.Empty;

    public string CategoryName { get; init; } = string.Empty;

    public string QuantityDisplay { get; init; } = string.Empty;

    public string RevenueDisplay { get; init; } = string.Empty;

    public string GrossProfitDisplay { get; init; } = string.Empty;

    public string MarginDisplay { get; init; } = string.Empty;

    public double BarWidth { get; init; }

    public decimal Revenue { get; init; }

    public decimal GrossProfit { get; init; }
}

public sealed class ProfitabilityCategoryRowViewModel
{
    public string Name { get; init; } = string.Empty;

    public string RevenueDisplay { get; init; } = string.Empty;

    public string GrossProfitDisplay { get; init; } = string.Empty;

    public string MarginDisplay { get; init; } = string.Empty;

    public string ContributionDisplay { get; init; } = string.Empty;

    public double BarWidth { get; init; }

    public decimal Revenue { get; init; }

    public decimal GrossProfit { get; init; }
}

public sealed class ProfitabilitySellerRowViewModel
{
    public string Name { get; init; } = string.Empty;

    public string NetSalesDisplay { get; init; } = string.Empty;

    public string GrossProfitDisplay { get; init; } = string.Empty;
}

public sealed class ProfitabilityAlertRowViewModel
{
    public string Message { get; init; } = string.Empty;

    public string SeverityLabel { get; init; } = string.Empty;

    public string SeverityColor { get; init; } = string.Empty;

    public string DetailDisplay { get; init; } = string.Empty;
}
