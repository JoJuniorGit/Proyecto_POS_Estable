using CommunityToolkit.Mvvm.ComponentModel;

namespace Desktop.Client.ViewModels;

/// <summary>
/// 8.212-WU4: one editable opening amount for a physical payment method. The WPF open overlay
/// derives the legacy local/USD aggregates from these rows before calling the server.
/// </summary>
public partial class OpeningPhysicalMethodViewModel : ObservableObject
{
    public int PaymentMethodId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string CurrencyCode { get; init; } = string.Empty;
    public string CurrencySymbol { get; init; } = string.Empty;

    [ObservableProperty]
    private string _amountText = "0";

    public string DisplayName => $"{Name} ({CurrencyCode})";
}
