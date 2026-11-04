using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Desktop.Client.Services;

namespace Desktop.Client.Converters;

// 8.211-T4c2b1 (S9): presentation-only converters for the WPF checkout. They read the active
// CurrencyDisplay profile at conversion time and never perform financial arithmetic; numeric
// separators remain governed by the configured CurrencyFormat through CurrencyDisplay.Number.

/// <summary>
/// Formats an amount in the local currency of the active profile. In <c>Single</c> mode it
/// collapses to the primary currency, matching <see cref="CurrencyDisplay.FormatSecondary"/>.
/// </summary>
public sealed class LocalAmountConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => CurrencyAmountValue.TryGetDecimal(value, out var amount)
            ? CurrencyDisplay.FormatSecondary(amount)
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException("Currency amounts are display-only.");
}

/// <summary>Formats an amount in the primary currency of the active profile.</summary>
public sealed class PrimaryAmountConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => CurrencyAmountValue.TryGetDecimal(value, out var amount)
            ? CurrencyDisplay.FormatPrimary(amount)
            : string.Empty;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException("Currency amounts are display-only.");
}

/// <summary>Exposes the configured symbol of the local (secondary in Dual) currency.</summary>
public sealed class LocalCurrencySymbolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => CurrencyDisplay.LocalCurrencySymbol;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException("Currency labels are display-only.");
}

/// <summary>Exposes the configured symbol of the primary currency.</summary>
public sealed class PrimaryCurrencySymbolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => CurrencyDisplay.PrimaryCurrencySymbol;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException("Currency labels are display-only.");
}

/// <summary>Exposes the ISO code of the local (secondary in Dual) currency.</summary>
public sealed class LocalCurrencyCodeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => CurrencyDisplay.LocalCurrencyCode;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException("Currency labels are display-only.");
}

/// <summary>Exposes the ISO code of the primary currency.</summary>
public sealed class PrimaryCurrencyCodeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => CurrencyDisplay.PrimaryCurrencyCode;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException("Currency labels are display-only.");
}

/// <summary>
/// Shows an element only when the active profile is <c>Dual</c>; collapses it in <c>Single</c>
/// so the checkout does not render a second-currency line that no longer exists. The bound
/// value is intentionally ignored: the mode comes from the shared <see cref="CurrencyDisplay"/>.
/// </summary>
public sealed class DualOnlyVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => CurrencyDisplay.IsSingle ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException("Visibility is display-only.");
}

/// <summary>
/// Defensive value adapter for bindings that may deliver <c>decimal</c>, a nullable decimal or
/// a boxed numeric primitive. Unknown or unrepresentable values yield an empty label instead of
/// throwing during rendering.
/// </summary>
internal static class CurrencyAmountValue
{
    public static bool TryGetDecimal(object? value, out decimal amount)
    {
        amount = 0m;
        if (value is null)
        {
            return false;
        }

        if (value is decimal decimalValue)
        {
            amount = decimalValue;
            return true;
        }

        if (value is not (double or float or long or int or short or byte))
        {
            return false;
        }

        try
        {
            amount = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
        catch (InvalidCastException)
        {
            return false;
        }
    }
}
