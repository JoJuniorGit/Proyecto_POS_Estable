using Desktop.Client.Services;

namespace Desktop.Client.ViewModels;

/// <summary>
/// S1/S2: total físico de una moneda en la gaveta. La moneda local del perfil se marca para
/// que la vista la destaque y la muestre primero; el monto se formatea con el ajuste activo.
/// </summary>
public sealed class PhysicalCurrencyBalanceViewModel
{
    public string CurrencyCode { get; init; } = string.Empty;
    public string CurrencyLabel { get; init; } = string.Empty;
    public string CurrencySymbol { get; init; } = string.Empty;
    public decimal CurrentBalance { get; init; }
    public bool IsLocal { get; init; }

    public string DisplayAmount => string.IsNullOrWhiteSpace(CurrencySymbol)
        ? CurrencyDisplay.Number(CurrentBalance, 2)
        : $"{CurrencySymbol} {CurrencyDisplay.Number(CurrentBalance, 2)}";
}
