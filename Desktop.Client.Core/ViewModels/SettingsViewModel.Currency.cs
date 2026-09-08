using CommunityToolkit.Mvvm.ComponentModel;
using Core.Common;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace Desktop.Client.ViewModels;

public partial class SettingsViewModel
{
    public class CurrencyFormatOption
    {
        public string Key { get; set; } = "Venezuelan";
        public string DisplayName { get; set; } = "Venezolano Contable (1.234,56)";
        public string Description { get; set; } = "Separador de miles: punto (.), decimal: coma (,)";
    }

    public ObservableCollection<CurrencyFormatOption> AvailableCurrencyFormats { get; } = new()
    {
        new CurrencyFormatOption { Key = "Venezuelan", DisplayName = "Venezolano Contable (1.234,56)", Description = "Separador de miles: punto (.), decimal: coma (,)" },
        new CurrencyFormatOption { Key = "International", DisplayName = "Internacional (1,234.56)", Description = "Separador de miles: coma (,), decimal: punto (.)" }
    };

    private CurrencyFormatOption? _selectedCurrencyFormat;
    public CurrencyFormatOption? SelectedCurrencyFormat
    {
        get => _selectedCurrencyFormat;
        set
        {
            if (SetProperty(ref _selectedCurrencyFormat, value))
            {
                OnSelectedCurrencyFormatChangedAsync(value).SafeFireAndForget("Settings.CurrencyFormatChanged");
            }
        }
    }

    [ObservableProperty]
    private string _currencyFormatPreviewBsS = "Bs.S 172.786,94";

    [ObservableProperty]
    private string _currencyFormatPreviewUSD = "$ 1.250,50";

    private async Task LoadCurrencyFormatAsync()
    {
        try
        {
            var savedFormat = await _settingsService.GetCurrencyFormatAsync();
            var selected = AvailableCurrencyFormats.FirstOrDefault(f => f.Key.Equals(savedFormat, StringComparison.OrdinalIgnoreCase))
                           ?? AvailableCurrencyFormats[0];
            _selectedCurrencyFormat = selected;
            OnPropertyChanged(nameof(SelectedCurrencyFormat));
            UpdateCurrencyPreview(selected.Key);
        }
        catch
        {
            _selectedCurrencyFormat = AvailableCurrencyFormats[0];
            OnPropertyChanged(nameof(SelectedCurrencyFormat));
            UpdateCurrencyPreview("Venezuelan");
        }
    }

    private async Task OnSelectedCurrencyFormatChangedAsync(CurrencyFormatOption? option)
    {
        if (option == null) return;
        try
        {
            UpdateCurrencyPreview(option.Key);
            await _settingsService.SetCurrencyFormatAsync(option.Key);
        }
        catch (Exception ex)
        {
            if (_dialogService != null) _dialogService.ShowError("Settings Error", $"Error al guardar formato de moneda: {ex.Message}");
        }
    }

    private void UpdateCurrencyPreview(string formatKey)
    {
        if (formatKey.Equals("International", StringComparison.OrdinalIgnoreCase))
        {
            CurrencyFormatPreviewBsS = "Bs.S 172,786.94";
            CurrencyFormatPreviewUSD = "$ 1,250.50";
        }
        else
        {
            CurrencyFormatPreviewBsS = "Bs.S 172.786,94";
            CurrencyFormatPreviewUSD = "$ 1.250,50";
        }
    }
}
