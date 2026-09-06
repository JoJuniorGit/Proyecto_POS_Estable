using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using System;
using System.Globalization;
using System.Threading.Tasks;

namespace Desktop.Client.ViewModels;

public partial class ExchangeRateViewModel : ObservableObject
{
    private readonly Services.IExchangeRateService _exchangeRateService;

    private decimal _currentRate;
    public decimal CurrentRate
    {
        get => _currentRate;
        set => SetProperty(ref _currentRate, value);
    }

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
            }
        }
    }

    public bool IsRateOutdated => LastUpdated == null || (DateTime.UtcNow - LastUpdated.Value.ToUniversalTime()) > TimeSpan.FromHours(24);

    public string LastUpdatedLocalFormatted
    {
        get
        {
            if (!LastUpdated.HasValue) return "Nunca";
            var utc = LastUpdated.Value.Kind == DateTimeKind.Utc
                ? LastUpdated.Value
                : DateTime.SpecifyKind(LastUpdated.Value, DateTimeKind.Utc);
            var local = Core.Helpers.TimeZoneHelper.ToVenezuelaTime(utc);
            return local.ToString("dd/MM/yyyy hh:mm tt", CultureInfo.CurrentCulture);
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

    private string? _statusMessage;
    public string? StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public ObservableCollection<Services.ExchangeRateHistoryDto> History { get; } = new();
    public Services.UserSession? UserSession { get; }

    public ExchangeRateViewModel(Services.IExchangeRateService exchangeRateService, Services.UserSession? userSession = null)
    {
        _exchangeRateService = exchangeRateService;
        UserSession = userSession;

        WeakReferenceMessenger.Default.Register<TimeZoneChangedMessage>(this, (_r, _m) =>
        {
            _ = LoadAllAsync();
        });

        if (UserSession == null || UserSession.IsLoggedIn)
        {
            _ = LoadAllAsync();
        }
    }

    public static bool TryParseRate(string? text, out decimal rate)
    {
        rate = 0m;
        if (string.IsNullOrWhiteSpace(text)) return false;
        string clean = text.Trim();

        int lastDot = clean.LastIndexOf('.');
        int lastComma = clean.LastIndexOf(',');

        if (lastComma > -1 && lastDot > -1)
        {
            if (lastComma > lastDot)
            {
                // Formato europeo/latino: 1.234,56 -> quitar puntos, coma pasa a punto
                clean = clean.Replace(".", "").Replace(',', '.');
            }
            else
            {
                // Formato anglosajón: 1,234.56 -> quitar comas
                clean = clean.Replace(",", "");
            }
        }
        else if (lastComma > -1)
        {
            // Solo comas: "813,74" -> reemplazar por punto
            clean = clean.Replace(',', '.');
        }

        if (decimal.TryParse(clean, NumberStyles.Any, CultureInfo.InvariantCulture, out rate))
            return rate > 0;

        if (decimal.TryParse(text.Trim(), NumberStyles.Any, CultureInfo.CurrentCulture, out rate))
            return rate > 0;

        return false;
    }

    [RelayCommand]
    private async Task LoadAllAsync()
    {
        if (UserSession != null && !UserSession.IsLoggedIn) return;

        IsLoading = true;
        StatusMessage = null;
        try
        {
            var (rate, lastUpdatedVal) = await _exchangeRateService.GetCurrentRateAsync();
            CurrentRate = rate;
            NewRateText = rate > 0 ? rate.ToString("0.00", CultureInfo.InvariantCulture) : "0.00";
            LastUpdated = lastUpdatedVal;

            await RefreshHistoryAsync();
        }
        catch (System.Exception ex)
        {
            StatusMessage = $"Error loading: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SaveRateAsync()
    {
        if (!TryParseRate(NewRateText, out var newRate) || newRate <= 0)
        {
            StatusMessage = "The exchange rate must be greater than zero.";
            return;
        }

        IsSaving = true;
        StatusMessage = null;
        try
        {
            // Save to backend and trigger local update via messaging
            await _exchangeRateService.SaveRateAsync(newRate);

            CurrentRate = newRate;
            NewRateText = newRate.ToString("0.00", CultureInfo.InvariantCulture);
            LastUpdated = DateTime.UtcNow;
            CanRetrySync = false;
            StatusMessage = "Tasa de cambio guardada correctamente.";

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
        IsSaving = true;
        StatusMessage = "Sincronizando con BCV...";
        CanRetrySync = false;
        try
        {
            var (rate, lastUpdatedVal) = await _exchangeRateService.SyncBcvAsync();
            if (rate > 0)
            {
                CurrentRate = rate;
                NewRateText = rate.ToString("0.00", CultureInfo.InvariantCulture);
                LastUpdated = lastUpdatedVal;
                StatusMessage = "Tasa BCV sincronizada correctamente.";

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
        var history = await _exchangeRateService.GetHistoryAsync();
        History.Clear();
        foreach (var item in history)
            History.Add(item);
    }
}
