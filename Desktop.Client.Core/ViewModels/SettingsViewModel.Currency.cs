using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using Core.Common;
using Desktop.Client.Messages;
using Desktop.Client.Services;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

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
            var previous = _selectedCurrencyFormat;
            if (SetProperty(ref _selectedCurrencyFormat, value))
            {
                OnSelectedCurrencyFormatChangedAsync(value, previous).SafeFireAndForget("Settings.CurrencyFormatChanged");
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
            // 8.143: el display de precios/checkout del WPF sigue el ajuste cargado.
            CurrencyDisplay.SetFromSetting(selected.Key);
            UpdateCurrencyPreview(selected.Key);
        }
        catch (Exception ex)
        {
            Core.Logging.ClientStateLogger.LogWarning(
                $"No se pudo cargar el formato de moneda; se usa el venezolano: {ex.Message}",
                nameof(SettingsViewModel));
            _selectedCurrencyFormat = AvailableCurrencyFormats[0];
            OnPropertyChanged(nameof(SelectedCurrencyFormat));
            CurrencyDisplay.SetFromSetting("Venezuelan");
            UpdateCurrencyPreview("Venezuelan");
        }
    }

    private async Task OnSelectedCurrencyFormatChangedAsync(CurrencyFormatOption? option, CurrencyFormatOption? previous)
    {
        if (option == null) return;
        try
        {
            // 8.143: aplica el formato al display del cliente antes de persistirlo y re-notifica
            // las vistas abiertas (el estado activo vive en CurrencyDisplay; el mensaje no lleva payload).
            CurrencyDisplay.SetFromSetting(option.Key);
            UpdateCurrencyPreview(option.Key);
            WeakReferenceMessenger.Default.Send(new CurrencyFormatChangedMessage());
            await _settingsService.SetCurrencyFormatAsync(option.Key);
        }
        catch (Exception ex)
        {
            // CLEAN-06: revierte el estado optimista para que la UI no quede mostrando un
            // formato que el servidor no confirmó.
            if (previous != null)
            {
                _selectedCurrencyFormat = previous;
                OnPropertyChanged(nameof(SelectedCurrencyFormat));
                CurrencyDisplay.SetFromSetting(previous.Key);
                UpdateCurrencyPreview(previous.Key);
                WeakReferenceMessenger.Default.Send(new CurrencyFormatChangedMessage());
            }

            Core.Logging.ClientStateLogger.LogError(
                $"Error al guardar formato de moneda: {ex.Message}",
                nameof(SettingsViewModel));
            _dialogService?.ShowError("Settings Error", $"Error al guardar formato de moneda: {ex.Message}");
        }
    }

    private void UpdateCurrencyPreview(string formatKey)
    {
        // 8.211-T4c2b4d (S9): the preview shows the active profile symbols, while separators
        // still come from the selected numeric format. In Single both previews collapse to the
        // primary currency; the view hides the duplicate.
        var localSymbol = CurrencyDisplay.LocalCurrencySymbol;
        var primarySymbol = CurrencyDisplay.PrimaryCurrencySymbol;
        if (formatKey.Equals("International", StringComparison.OrdinalIgnoreCase))
        {
            CurrencyFormatPreviewBsS = $"{localSymbol} 172,786.94";
            CurrencyFormatPreviewUSD = $"{primarySymbol} 1,250.50";
        }
        else
        {
            CurrencyFormatPreviewBsS = $"{localSymbol} 172.786,94";
            CurrencyFormatPreviewUSD = $"{primarySymbol} 1.250,50";
        }
    }
}
