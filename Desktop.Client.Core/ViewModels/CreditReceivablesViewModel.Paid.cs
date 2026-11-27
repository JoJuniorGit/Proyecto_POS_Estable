using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

/// <summary>
/// CxC WPF: subseccion Deudas Pagadas (historial realizado) y cronologia de abonos en slide-over.
/// La cartera pendiente conserva su flujo; estos estados viven en una partial para no mezclar
/// responsabilidades ni contaminar la tabla operativa.
/// </summary>
public partial class CreditReceivablesViewModel
{
    private int _paidLoadGeneration;
    private List<int> _timelineNavigationIds = new();
    private int _timelineNavigationIndex = -1;

    public ObservableCollection<PaidReceivableClientItemDto> PaidDebts { get; } = new();
    public ObservableCollection<PaidReceivablePaymentItemClientDto> PaidPayments { get; } = new();
    public ObservableCollection<PaymentMethodDto> PaidMethods { get; } = new();

    [ObservableProperty] private bool _isPaidSelected;
    [ObservableProperty] private bool _isPaidLoading;
    [ObservableProperty] private string? _paidError;
    [ObservableProperty] private string _paidSearchQuery = string.Empty;
    [ObservableProperty] private DateTime? _paidFrom;
    [ObservableProperty] private DateTime? _paidTo;
    [ObservableProperty] private PaymentMethodDto? _selectedPaidMethod;
    [ObservableProperty] private PaidReceivablesSummaryClientDto _paidSummary = new();
    [ObservableProperty] private bool _isPaidPaymentMode;
    [ObservableProperty] private int _paidPage = 1;
    [ObservableProperty] private int _paidPageSize = 25;
    [ObservableProperty] private int _paidTotalCount;
    [ObservableProperty] private bool _paidHasMore;
    [ObservableProperty] private string? _settledNotice;
    [ObservableProperty] private bool _showViewPaidLink;

    [ObservableProperty] private bool _isTimelineOpen;
    [ObservableProperty] private bool _isTimelineLoading;
    [ObservableProperty] private string? _timelineError;
    [ObservableProperty] private ReceivablePaymentTimelineClientDto? _timeline;

    public bool IsPendingSelected => !IsPaidSelected;

    public bool IsPaidInvoiceMode => !IsPaidPaymentMode;

    public string PaidPageLabel
    {
        get
        {
            int pages = PaidPageSize <= 0 ? 1 : Math.Max(1, (int)Math.Ceiling(PaidTotalCount / (double)PaidPageSize));
            return $"Página {Math.Min(PaidPage, pages)} de {pages} · {PaidTotalCount} deudas";
        }
    }

    public string PaidDebtsCountDisplay => PaidSummary.PaidDebtsCount.ToString("N0");

    public string PaidCollectedUsdDisplay => CurrencyDisplay.FormatPrimary(PaidSummary.TotalCollectedUsd);

    public string PaidCollectedLocalDisplay => CurrencyDisplay.FormatSecondary(PaidSummary.TotalCollectedLocalHistorical);

    public string PaidPaymentsCountDisplay => PaidSummary.TotalPaymentsCount.ToString("N0");

    public string PaidAverageDaysDisplay => PaidSummary.AverageDaysDisplay;

    public bool CanPaidPreviousPage => PaidPage > 1 && !IsPaidLoading;

    public bool CanPaidNextPage => PaidHasMore && !IsPaidLoading;

    public bool CanTimelinePrevious => _timelineNavigationIndex > 0;

    public bool CanTimelineNext => _timelineNavigationIndex >= 0
        && _timelineNavigationIndex < _timelineNavigationIds.Count - 1;

    public bool ShowPaidEmptyState => !IsPaidLoading && !IsPaidPaymentMode && PaidDebts.Count == 0;

    public bool ShowPaidPaymentsEmptyState => !IsPaidLoading && IsPaidPaymentMode && PaidPayments.Count == 0;

    partial void OnIsPaidSelectedChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPendingSelected));
    }

    partial void OnIsPaidPaymentModeChanged(bool value)
    {
        OnPropertyChanged(nameof(IsPaidInvoiceMode));
        OnPropertyChanged(nameof(ShowPaidEmptyState));
        OnPropertyChanged(nameof(ShowPaidPaymentsEmptyState));
    }

    partial void OnPaidPageChanged(int value)
    {
        OnPropertyChanged(nameof(PaidPageLabel));
        OnPropertyChanged(nameof(CanPaidPreviousPage));
        OnPropertyChanged(nameof(CanPaidNextPage));
    }

    partial void OnPaidTotalCountChanged(int value)
    {
        OnPropertyChanged(nameof(PaidPageLabel));
        OnPropertyChanged(nameof(CanPaidNextPage));
    }

    partial void OnPaidHasMoreChanged(bool value) => OnPropertyChanged(nameof(CanPaidNextPage));

    partial void OnIsPaidLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanPaidPreviousPage));
        OnPropertyChanged(nameof(CanPaidNextPage));
        OnPropertyChanged(nameof(ShowPaidEmptyState));
        OnPropertyChanged(nameof(ShowPaidPaymentsEmptyState));
    }

    partial void OnPaidSummaryChanged(PaidReceivablesSummaryClientDto value)
    {
        OnPropertyChanged(nameof(PaidDebtsCountDisplay));
        OnPropertyChanged(nameof(PaidCollectedUsdDisplay));
        OnPropertyChanged(nameof(PaidCollectedLocalDisplay));
        OnPropertyChanged(nameof(PaidPaymentsCountDisplay));
        OnPropertyChanged(nameof(PaidAverageDaysDisplay));
    }

    [RelayCommand]
    private async Task SelectPendingAsync()
    {
        IsPaidSelected = false;
        await EnsureLoadedAsync();
    }

    [RelayCommand]
    private async Task SelectPaidAsync()
    {
        IsPaidSelected = true;
        await EnsurePaidLoadedAsync();
    }

    [RelayCommand]
    private async Task SetPaidPresetAsync(string? preset)
    {
        DateTime today = DateTime.Today;
        switch (preset)
        {
            case "Hoy":
                PaidFrom = today;
                PaidTo = today;
                break;
            case "Semana":
                int diff = ((int)today.DayOfWeek + 6) % 7;
                PaidFrom = today.AddDays(-diff);
                PaidTo = today;
                break;
            case "Mes":
                PaidFrom = new DateTime(today.Year, today.Month, 1);
                PaidTo = today;
                break;
            default:
                PaidFrom = null;
                PaidTo = null;
                break;
        }

        PaidPage = 1;
        await EnsurePaidLoadedAsync();
    }

    [RelayCommand]
    private async Task SetPaidViewModeAsync(string? mode)
    {
        IsPaidPaymentMode = string.Equals(mode, "payment", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mode, "abono", StringComparison.OrdinalIgnoreCase);
        PaidPage = 1;
        await EnsurePaidLoadedAsync();
    }

    [RelayCommand]
    private void ClearPaidSearch() => PaidSearchQuery = string.Empty;

    [RelayCommand]
    private async Task ApplyPaidFiltersAsync()
    {
        PaidPage = 1;
        await EnsurePaidLoadedAsync();
    }

    [RelayCommand]
    private async Task ClearPaidFiltersAsync()
    {
        PaidSearchQuery = string.Empty;
        PaidFrom = null;
        PaidTo = null;
        SelectedPaidMethod = null;
        PaidPage = 1;
        await EnsurePaidLoadedAsync();
    }

    [RelayCommand]
    private async Task PaidNextPageAsync()
    {
        if (!CanPaidNextPage) return;
        PaidPage++;
        await EnsurePaidLoadedAsync();
    }

    [RelayCommand]
    private async Task PaidPreviousPageAsync()
    {
        if (!CanPaidPreviousPage) return;
        PaidPage = Math.Max(1, PaidPage - 1);
        await EnsurePaidLoadedAsync();
    }

    [RelayCommand]
    private async Task GoToPaidAsync()
    {
        SettledNotice = null;
        ShowViewPaidLink = false;
        IsPaidSelected = true;
        await EnsurePaidLoadedAsync();
    }

    [RelayCommand]
    private async Task OpenTimelineAsync(object? parameter)
    {
        int obligationId = ResolveObligationId(parameter);
        if (obligationId <= 0) return;

        _timelineNavigationIds = IsPaidSelected
            ? PaidDebts.Select(item => item.ObligationId).ToList()
            : Receivables.Select(item => item.ReceivableId).ToList();
        _timelineNavigationIndex = _timelineNavigationIds.IndexOf(obligationId);

        await LoadTimelineAsync(obligationId);
    }

    [RelayCommand]
    private void CloseTimeline()
    {
        IsTimelineOpen = false;
        Timeline = null;
        TimelineError = null;
        _timelineNavigationIds = new();
        _timelineNavigationIndex = -1;
        OnPropertyChanged(nameof(CanTimelinePrevious));
        OnPropertyChanged(nameof(CanTimelineNext));
    }

    [RelayCommand]
    private async Task TimelinePreviousAsync()
    {
        if (!CanTimelinePrevious) return;
        _timelineNavigationIndex--;
        await LoadTimelineAsync(_timelineNavigationIds[_timelineNavigationIndex]);
    }

    [RelayCommand]
    private async Task TimelineNextAsync()
    {
        if (!CanTimelineNext) return;
        _timelineNavigationIndex++;
        await LoadTimelineAsync(_timelineNavigationIds[_timelineNavigationIndex]);
    }

    public async Task EnsurePaidLoadedAsync()
    {
        if (_userSession != null && !_userSession.IsLoggedIn) return;

        int generation = ++_paidLoadGeneration;
        IsPaidLoading = true;
        PaidError = null;

        try
        {
            if (PaidMethods.Count == 0)
            {
                var methods = await _paymentService.GetActiveMethodsAsync();
                foreach (var method in methods)
                {
                    PaidMethods.Add(method);
                }
            }

            var filter = BuildPaidFilter();
            if (IsPaidPaymentMode)
            {
                var result = await _salesService.GetPaidReceivablePaymentsAsync(filter);
                if (generation != _paidLoadGeneration) return;
                PaidPayments.Clear();
                foreach (var item in result.Items)
                {
                    PaidPayments.Add(item);
                }

                PaidSummary = result.Summary;
                PaidTotalCount = result.TotalCount;
                PaidHasMore = result.HasMore;
            }
            else
            {
                var result = await _salesService.GetPaidReceivablesAsync(filter);
                if (generation != _paidLoadGeneration) return;
                PaidDebts.Clear();
                foreach (var item in result.Items)
                {
                    PaidDebts.Add(item);
                }

                PaidSummary = result.Summary;
                PaidTotalCount = result.TotalCount;
                PaidHasMore = result.HasMore;
            }
        }
        catch (System.Net.Http.HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            // La sesión la gestiona MainViewModel.
        }
        catch (Exception ex)
        {
            PaidError = $"Error al cargar el historial de deudas pagadas: {ex.Message}";
        }
        finally
        {
            if (generation == _paidLoadGeneration) IsPaidLoading = false;
        }
    }

    private PaidReceivablesFilterClientDto BuildPaidFilter() => new()
    {
        Search = string.IsNullOrWhiteSpace(PaidSearchQuery) ? null : PaidSearchQuery.Trim(),
        PaidFrom = PaidFrom,
        PaidTo = PaidTo,
        PaymentMethodId = SelectedPaidMethod?.Id,
        Page = PaidPage,
        PageSize = PaidPageSize
    };

    private async Task LoadTimelineAsync(int obligationId)
    {
        IsTimelineOpen = true;
        IsTimelineLoading = true;
        TimelineError = null;
        Timeline = null;
        OnPropertyChanged(nameof(CanTimelinePrevious));
        OnPropertyChanged(nameof(CanTimelineNext));

        try
        {
            Timeline = await _salesService.GetReceivablePaymentTimelineAsync(obligationId);
        }
        catch (Exception ex)
        {
            TimelineError = $"No se pudo cargar la cronología de abonos: {ex.Message}";
        }
        finally
        {
            IsTimelineLoading = false;
        }
    }

    private static int ResolveObligationId(object? parameter) => parameter switch
    {
        PaidReceivableClientItemDto paid => paid.ObligationId,
        PaidReceivablePaymentItemClientDto payment => payment.Allocations.FirstOrDefault()?.ObligationId ?? 0,
        CreditReceivableClientItemDto pending => pending.ReceivableId,
        int value => value,
        _ => 0
    };


    [RelayCommand]
    private async Task ExportPaidAsync(string? format)
    {
        string normalized = (format ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized != "csv")
        {
            _dialogService.ShowWarning("Exportación", "La exportación Excel/PDF del historial se habilitará en la verificación final (WU3).");
            return;
        }

        var sb = new System.Text.StringBuilder();
        if (IsPaidPaymentMode)
        {
            sb.AppendLine("Fecha;Cajero;Cliente;Documento;Facturas;Metodo;Moneda;Monto cobrado;Tasa;Acreditado USD;Referencia;Imputacion");
            foreach (var item in PaidPayments)
            {
                sb.AppendLine(string.Join(';',
                    Escape(item.PaidAtDisplay),
                    Escape(item.CashierName),
                    Escape(item.CustomerName),
                    Escape(item.CustomerDocument),
                    Escape(item.InvoiceSummary),
                    Escape(item.PaymentMethodName),
                    Escape(item.PaymentCurrency),
                    Escape(CurrencyDisplay.Number(item.AmountPaidInCurrency)),
                    Escape(CurrencyDisplay.Number(item.ExchangeRateApplied)),
                    Escape(CurrencyDisplay.Number(item.AmountCreditedUsd)),
                    Escape(item.Reference ?? string.Empty),
                    Escape(item.ImputationMode)));
            }
        }
        else
        {
            sb.AppendLine("Documento;Cliente;Documento cliente;Emision;Pagada;Dias;A tiempo;Total pagado USD;Total pagado Bs.S historico;Abonos;Metodos");
            foreach (var item in PaidDebts)
            {
                sb.AppendLine(string.Join(';',
                    Escape(item.InvoiceDisplay),
                    Escape(item.CustomerName),
                    Escape(item.CustomerDocument),
                    Escape(item.IssuedAtDisplay),
                    Escape(item.PaidAtDisplay),
                    Escape(item.DaysToSettle.ToString()),
                    Escape(item.SettledOnTimeLabel),
                    Escape(CurrencyDisplay.Number(item.TotalPaidUsd)),
                    Escape(CurrencyDisplay.Number(item.TotalPaidLocalHistorical)),
                    Escape(item.PaymentsCount.ToString()),
                    Escape(item.PaymentMethodsSummary)));
            }
        }

        byte[] payload = System.Text.Encoding.UTF8.GetBytes(sb.ToString());
        byte[] bom = System.Text.Encoding.UTF8.GetPreamble();
        byte[] file = new byte[bom.Length + payload.Length];
        Buffer.BlockCopy(bom, 0, file, 0, bom.Length);
        Buffer.BlockCopy(payload, 0, file, bom.Length, payload.Length);
        await ReceiptOpener(file, $"DeudasPagadas_{DateTime.Now:yyyyMMdd_HHmm}.csv");
    }

    private static string Escape(string value)
    {
        string text = value ?? string.Empty;
        return text.Contains(';') || text.Contains('"') || text.Contains('\n')
            ? '"' + text.Replace("\"", "\"\"") + '"'
            : text;
    }
}

