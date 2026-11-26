using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Core.Common;
using Core.Helpers;
using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class ExchangeRateViewModel : ObservableObject, IDisposable
{
    private const int HistoryWindowSize = 365;

    private readonly Services.IExchangeRateService _exchangeRateService;

    private decimal _currentRate;
    public decimal CurrentRate
    {
        get => _currentRate;
        set
        {
            if (SetProperty(ref _currentRate, value))
            {
                OnPropertyChanged(nameof(CurrentRateDisplay));
            }
        }
    }

    public string CurrentRateDisplay => Services.CurrencyDisplay.Number(CurrentRate, 4);

    private string _newRateText = "0.00";
    public string NewRateText
    {
        get => _newRateText;
        set => SetProperty(ref _newRateText, value);
    }

    private DateTime? _lastUpdated;
    public DateTime? LastUpdated
    {
        get => _lastUpdated;
        set
        {
            if (SetProperty(ref _lastUpdated, value))
            {
                OnPropertyChanged(nameof(IsRateOutdated));
                OnPropertyChanged(nameof(LastUpdatedLocalFormatted));
                OnPropertyChanged(nameof(LastUpdatedStatusText));
                OnPropertyChanged(nameof(LastUpdatedDotColor));
            }
        }
    }

    public bool IsRateOutdated => LastUpdated == null || (DateTime.UtcNow - LastUpdated.Value.ToUniversalTime()) > TimeSpan.FromHours(24);

    public string LastUpdatedStatusText
    {
        get
        {
            if (!LastUpdated.HasValue) return "Sin actualización registrada";
            var utc = LastUpdated.Value.Kind == DateTimeKind.Utc
                ? LastUpdated.Value
                : DateTime.SpecifyKind(LastUpdated.Value, DateTimeKind.Utc);
            var local = Core.Helpers.TimeZoneHelper.ToVenezuelaTime(utc);
            return $"Actualizada: {local.ToString("dd/MM/yyyy · hh:mm tt", MoneyFormat.Culture)}";
        }
    }

    public string LastUpdatedDotColor => IsRateOutdated ? "#F59E0B" : "#16A34A";

    public string LastUpdatedLocalFormatted
    {
        get
        {
            if (!LastUpdated.HasValue) return "Nunca";
            var utc = LastUpdated.Value.Kind == DateTimeKind.Utc
                ? LastUpdated.Value
                : DateTime.SpecifyKind(LastUpdated.Value, DateTimeKind.Utc);
            var local = Core.Helpers.TimeZoneHelper.ToVenezuelaTime(utc);
            return local.ToString("dd/MM/yyyy hh:mm tt", MoneyFormat.Culture);
        }
    }

    private bool _canRetrySync;
    public bool CanRetrySync
    {
        get => _canRetrySync;
        set => SetProperty(ref _canRetrySync, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        set => SetProperty(ref _isLoading, value);
    }

    private bool _isSaving;
    public bool IsSaving
    {
        get => _isSaving;
        set => SetProperty(ref _isSaving, value);
    }

    private bool _isHistoryLoading;
    public bool IsHistoryLoading
    {
        get => _isHistoryLoading;
        set
        {
            if (SetProperty(ref _isHistoryLoading, value))
            {
                OnPropertyChanged(nameof(HasNoHistoryRecords));
            }
        }
    }

    public bool HasHistoryRecords => HistoryRows.Count > 0;

    public bool HasNoHistoryRecords => !IsHistoryLoading && !HasHistoryRecords;

    /// <summary>
    /// 8.211 parity: en régimen <c>Single</c> no existe tasa de cambio. La vista oculta la tasa
    /// vigente, el formulario, la sincronización y el historial; este aviso explica el motivo.
    /// </summary>
    public bool IsSingle => Services.CurrencyDisplay.IsSingle;

    public string SingleModeNotice =>
        $"El régimen monetario Single no utiliza tasa de cambio: los importes se expresan únicamente en {Services.CurrencyDisplay.LocalCurrencySymbol}. No hay sincronización BCV ni carga manual disponibles.";

    private string? _statusMessage;
    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public ObservableCollection<Services.ExchangeRateHistoryDto> History { get; } = new();
    public ObservableCollection<ExchangeRateHistoryRowViewModel> HistoryRows { get; } = new();
    public Services.UserSession? UserSession { get; }

    public ExchangeRateViewModel(Services.IExchangeRateService exchangeRateService, Services.UserSession? userSession = null)
    {
        _exchangeRateService = exchangeRateService;
        UserSession = userSession;

        HistoryRows.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasHistoryRecords));
            OnPropertyChanged(nameof(HasNoHistoryRecords));
        };

        Services.CurrencyDisplay.ProfileChanged += OnCurrencyDisplayProfileChanged;

        WeakReferenceMessenger.Default.Register<TimeZoneChangedMessage>(this, (_r, _m) =>
        {
            LoadAllAsync().SafeFireAndForget("ExchangeRateViewModel.TimeZoneChanged");
        });

        if (UserSession == null || UserSession.IsLoggedIn)
        {
            LoadAllAsync().SafeFireAndForget("ExchangeRateViewModel.InitialLoad");
        }
    }

    public static bool TryParseRate(string? text, out decimal rate)
    {
        if (FlexibleDecimalParser.TryParse(text, out rate))
        {
            return rate > 0;
        }

        // Fallback para configuraciones regionales que el parser tolerante no cubre.
        if (!string.IsNullOrWhiteSpace(text)
            && decimal.TryParse(text.Trim(), NumberStyles.Any, CultureInfo.CurrentCulture, out rate))
        {
            return rate > 0;
        }

        rate = 0m;
        return false;
    }

    [RelayCommand]
    private async Task LoadAllAsync()
    {
        if (UserSession != null && !UserSession.IsLoggedIn) return;
        if (IsLoading) return;

        if (IsSingle)
        {
            ClearHistory();
            return;
        }

        IsLoading = true;
        IsHistoryLoading = true;
        StatusMessage = null;
        try
        {
            var (rate, lastUpdatedVal) = await _exchangeRateService.GetCurrentRateAsync();
            CurrentRate = rate;
            NewRateText = Services.CurrencyDisplay.Number(rate > 0 ? rate : 0m, 2);
            LastUpdated = lastUpdatedVal;

            await RefreshHistoryAsync();
        }
        catch (System.Exception ex)
        {
            StatusMessage = $"Error al cargar: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
            IsHistoryLoading = false;
        }
    }

    [RelayCommand]
    private async Task SaveRateAsync()
    {
        if (IsSingle) return;

        if (!TryParseRate(NewRateText, out var newRate) || newRate <= 0)
        {
            StatusMessage = "La tasa de cambio debe ser mayor que cero.";
            return;
        }

        IsSaving = true;
        StatusMessage = null;
        try
        {
            // Save to backend and trigger local update via messaging
            await _exchangeRateService.SaveRateAsync(newRate);

            CurrentRate = _exchangeRateService.CurrentRate;
            NewRateText = Services.CurrencyDisplay.Number(CurrentRate, 2);
            LastUpdated = DateTime.UtcNow;
            CanRetrySync = false;
            StatusMessage = "Tasa de cambio actualizada correctamente.";

            // Refresh history
            await RefreshHistoryAsync();
        }
        catch (System.Exception ex)
        {
            StatusMessage = $"Error al guardar: {ex.Message}";
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private async Task SyncBcvAsync()
    {
        if (IsSingle) return;

        IsSaving = true;
        StatusMessage = "Sincronizando con BCV...";
        CanRetrySync = false;
        try
        {
            var (rate, lastUpdatedVal) = await _exchangeRateService.SyncBcvAsync();
            if (rate > 0)
            {
                CurrentRate = rate;
                NewRateText = Services.CurrencyDisplay.Number(rate, 2);
                LastUpdated = lastUpdatedVal;
                StatusMessage = $"Tasa sincronizada con BCV: {Services.CurrencyDisplay.FormatSecondary(rate, 4)}";

                // Refresh history
                await RefreshHistoryAsync();
            }
        }
        catch (System.Exception ex)
        {
            CanRetrySync = true;
            StatusMessage = $"{ex.Message} Puede reintentar la sincronización o ingresar la tasa del día manualmente.";
        }
        finally
        {
            IsSaving = false;
        }
    }

    private async Task RefreshHistoryAsync()
    {
        if (IsSingle)
        {
            ClearHistory();
            return;
        }

        IsHistoryLoading = true;
        try
        {
            var history = await _exchangeRateService.GetHistoryAsync();
            if (IsSingle)
            {
                ClearHistory();
                return;
            }

            var visible = history.Take(HistoryWindowSize).ToList();

            History.Clear();
            HistoryRows.Clear();
            for (var index = 0; index < visible.Count; index++)
            {
                var item = visible[index];
                History.Add(item);
                var previous = index + 1 < visible.Count ? visible[index + 1] : null;
                HistoryRows.Add(new ExchangeRateHistoryRowViewModel(item, index == 0, previous));
            }
        }
        finally
        {
            IsHistoryLoading = false;
        }
    }

    private void ClearHistory()
    {
        History.Clear();
        HistoryRows.Clear();
        OnPropertyChanged(nameof(HasHistoryRecords));
        OnPropertyChanged(nameof(HasNoHistoryRecords));
    }

    private void OnCurrencyDisplayProfileChanged()
    {
        OnPropertyChanged(nameof(IsSingle));
        OnPropertyChanged(nameof(SingleModeNotice));

        if (IsSingle)
        {
            ClearHistory();
            IsHistoryLoading = false;
            return;
        }

        if (UserSession == null || UserSession.IsLoggedIn)
        {
            LoadAllAsync().SafeFireAndForget("ExchangeRateViewModel.MonetaryProfileChanged");
        }
    }

    public void Dispose()
    {
        Services.CurrencyDisplay.ProfileChanged -= OnCurrencyDisplayProfileChanged;
        WeakReferenceMessenger.Default.UnregisterAll(this);
    }
}

/// <summary>
/// Fila de presentación del historial de tasas: formato monetario único del sistema, estado
/// Vigente para la primera fila y variación contra la tasa inmediatamente anterior cargada.
/// </summary>
public sealed class ExchangeRateHistoryRowViewModel
{
    public ExchangeRateHistoryRowViewModel(
        Services.ExchangeRateHistoryDto item,
        bool isCurrent,
        Services.ExchangeRateHistoryDto? previous)
    {
        ArgumentNullException.ThrowIfNull(item);

        DateDisplay = item.Date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        RateDisplay = Services.CurrencyDisplay.Number(item.Rate, 4);
        UpdatedAtDisplay = item.UpdatedAtLocal.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        IsCurrent = isCurrent;
        StatusLabel = isCurrent ? "Vigente" : "Histórica";
        StatusBackground = isCurrent ? "#DCFCE7" : "#F1F5F9";
        StatusForeground = isCurrent ? "#15803D" : "#64748B";

        if (previous is null)
        {
            VariationDisplay = "—";
            VariationForeground = "#94A3B8";
        }
        else
        {
            var variation = item.Rate - previous.Rate;
            VariationDisplay = variation > 0
                ? $"+{Services.CurrencyDisplay.Number(variation, 4)}"
                : Services.CurrencyDisplay.Number(variation, 4);
            VariationForeground = variation > 0
                ? "#15803D"
                : variation < 0 ? "#B91C1C" : "#64748B";
        }
    }

    public string DateDisplay { get; }
    public string RateDisplay { get; }
    public string VariationDisplay { get; }
    public string VariationForeground { get; }
    public string UpdatedAtDisplay { get; }
    public bool IsCurrent { get; }
    public string StatusLabel { get; }
    public string StatusBackground { get; }
    public string StatusForeground { get; }
}
