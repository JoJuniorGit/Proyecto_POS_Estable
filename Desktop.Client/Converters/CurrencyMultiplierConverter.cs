using System;
using System.Globalization;
using System.Windows.Data;
using Core.Helpers;
using Desktop.Client.Services;

namespace Desktop.Client.Converters;

/// <summary>
/// Multiplies a decimal value by an exchange rate for local currency display.
/// Usage: MultiBinding with [0] = decimal value, [1] = decimal exchangeRate
/// ConverterParameter = optional format string (default: "{0:N2}")
/// </summary>
public class CurrencyMultiplierConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2) return CurrencyDisplay.Number(0m);

        if (values[0] is decimal amount && values[1] is decimal rate)
        {
            var local = Math.Round(amount * rate, 2, MidpointRounding.AwayFromZero);
            var format = parameter as string ?? "{0:N2}";
            // 8.143: la cultura del display la fija el ajuste de moneda de la app, no la del SO.
            var displayCulture = CurrencyDisplay.Current == MoneyDisplayFormat.International
                ? CultureInfo.InvariantCulture
                : MoneyFormat.Culture;
            return string.Format(displayCulture, format, local);
        }

        return CurrencyDisplay.Number(0m);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
