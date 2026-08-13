using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
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
        set => SetProperty(ref _last_updated, value);
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
    public Services.UserSession UserSession { get; }

    public ExchangeRateViewModel(Services.IExchangeRateService exchange_rate_service, Services.UserSession userSession)
    {
        _exchange_rate_service = exchange_rate_service;
        UserSession = userSession;
        _ = LoadAllAsync();
    }

    [RelayCommand]
    private async Task LoadAllAsync()
    {
        IsLoading = true;
        StatusMessage = null;
        try
        {
            var (_rate, _last_updated_val) = await _exchange_rate_service.GetCurrentRateAsync();
            CurrentRate = _rate;
            NewRateText = _rate.ToString("N2");
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
        if (!decimal.TryParse(NewRateText, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out var _new_rate) || _new_rate <= 0)
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
            LastUpdated = DateTime.UtcNow;
            StatusMessage = "Exchange rate saved successfully.";

            // Refresh history
            await RefreshHistoryAsync();
        }
        catch (System.Exception _ex)
        {
            StatusMessage = $"Error saving: {_ex.Message}";
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
        StatusMessage = "Syncing with BCV...";
        try
        {
            var (_rate, _last_updated_val) = await _exchange_rate_service.SyncBcvAsync();
            if (_rate > 0)
            {
                CurrentRate = _rate;
                NewRateText = _rate.ToString("N2");
                LastUpdated = _last_updated_val;
                StatusMessage = "BCV Exchange rate synced successfully.";

                // Refresh history
                await RefreshHistoryAsync();
            }
        }
        catch (System.Exception _ex)
        {
            StatusMessage = $"Error syncing with BCV: {_ex.Message}";
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
