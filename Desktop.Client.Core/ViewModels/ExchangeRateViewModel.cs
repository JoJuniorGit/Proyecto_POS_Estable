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
    private readonly Services.IExchangeRateService _exchange_rate_service;

    private decimal _current_rate;
    public decimal CurrentRate
    {
        get => _current_rate;
        set => SetProperty(ref _current_rate, value);
    }

    private string _new_rate_text = "0.00";
    public string NewRateText
    {
        get => _new_rate_text;
        set => SetProperty(ref _new_rate_text, value);
    }

    private DateTime? _last_updated;
    public DateTime? LastUpdated
    {
        get => _last_updated;
        set
        {
            if (SetProperty(ref _last_updated, value))
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

    private bool _can_retry_sync;
    public bool CanRetrySync
    {
        get => _can_retry_sync;
        set => SetProperty(ref _can_retry_sync, value);
    }

    private bool _is_loading;
    public bool IsLoading
    {
        get => _is_loading;
        set => SetProperty(ref _is_loading, value);
    }

    private bool _is_saving;
    public bool IsSaving
    {
        get => _is_saving;
        set => SetProperty(ref _is_saving, value);
    }

    private string? _status_message;
    public string? StatusMessage
    {
        get => _status_message;
        set => SetProperty(ref _status_message, value);
    }

    public ObservableCollection<Services.ExchangeRateHistoryDto> History { get; } = new();
    public Services.UserSession? UserSession { get; }

    public ExchangeRateViewModel(Services.IExchangeRateService exchange_rate_service, Services.UserSession? userSession = null)
    {
        _exchange_rate_service = exchange_rate_service;
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
            var (_rate, _last_updated_val) = await _exchange_rate_service.GetCurrentRateAsync();
            CurrentRate = _rate;
            NewRateText = _rate > 0 ? _rate.ToString("0.00", CultureInfo.InvariantCulture) : "0.00";
            LastUpdated = _last_updated_val;

            await RefreshHistoryAsync();
        }
        catch (System.Exception _ex)
        {
            StatusMessage = $"Error loading: {_ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private async Task SaveRateAsync()
    {
        if (!TryParseRate(NewRateText, out var _new_rate) || _new_rate <= 0)
        {
            StatusMessage = "The exchange rate must be greater than zero.";
            return;
        }

        IsSaving = true;
        StatusMessage = null;
        try
        {
            // Save to backend and trigger local update via messaging
            await _exchange_rate_service.SaveRateAsync(_new_rate);

            CurrentRate = _new_rate;
            NewRateText = _new_rate.ToString("0.00", CultureInfo.InvariantCulture);
            LastUpdated = DateTime.UtcNow;
            CanRetrySync = false;
            StatusMessage = "Tasa de cambio guardada correctamente.";

            // Refresh history
            await RefreshHistoryAsync();
        }
        catch (System.Exception _ex)
        {
            StatusMessage = $"Error al guardar: {_ex.Message}";
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
            var (_rate, _last_updated_val) = await _exchange_rate_service.SyncBcvAsync();
            if (_rate > 0)
            {
                CurrentRate = _rate;
                NewRateText = _rate.ToString("0.00", CultureInfo.InvariantCulture);
                LastUpdated = _last_updated_val;
                StatusMessage = "Tasa BCV sincronizada correctamente.";

                // Refresh history
                await RefreshHistoryAsync();
            }
        }
        catch (System.Exception _ex)
        {
            CanRetrySync = true;
            StatusMessage = $"{_ex.Message} Puede reintentar la sincronización o ingresar la tasa del día manualmente.";
        }
        finally
        {
            IsSaving = false;
        }
    }

    private async Task RefreshHistoryAsync()
    {
        var _history = await _exchange_rate_service.GetHistoryAsync();
        History.Clear();
        foreach (var _item in _history)
            History.Add(_item);
    }
}
