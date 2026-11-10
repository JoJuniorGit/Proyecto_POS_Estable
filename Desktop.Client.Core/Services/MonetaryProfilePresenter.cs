using System.ComponentModel;
using Core.DTOs;

namespace Desktop.Client.Services;

/// <summary>
/// 8.211-T6-U12 (S9): observable presentation state for WPF. Views bind labels and visibility to
/// this singleton instead of static <see cref="CurrencyDisplay"/> values, so a profile refresh
/// raises PropertyChanged without recreating the view. Amount converters still read CurrencyDisplay
/// at conversion time; the presenter is the notification trigger for the bindings.
/// </summary>
public sealed class MonetaryProfilePresenter : INotifyPropertyChanged
{
    public static MonetaryProfilePresenter Instance { get; } = new();

    private MonetaryProfilePresenter()
    {
        CurrencyDisplay.ProfileChanged += Refresh;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public MonetaryMode Mode => CurrencyDisplay.Mode;
    public bool IsSingle => CurrencyDisplay.IsSingle;
    public string PrimaryCurrencyCode => CurrencyDisplay.PrimaryCurrencyCode;
    public string PrimaryCurrencySymbol => CurrencyDisplay.PrimaryCurrencySymbol;
    public string? SecondaryCurrencyCode => CurrencyDisplay.SecondaryCurrencyCode;
    public string? SecondaryCurrencySymbol => CurrencyDisplay.SecondaryCurrencySymbol;
    public string LocalCurrencyCode => CurrencyDisplay.LocalCurrencyCode;
    public string LocalCurrencySymbol => CurrencyDisplay.LocalCurrencySymbol;

    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
}
