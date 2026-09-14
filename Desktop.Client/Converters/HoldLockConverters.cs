using System;
using System.Globalization;
using System.Windows.Data;
using Core.DTOs;

namespace Desktop.Client.Converters;

public class HoldLockDisplayConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not SaleDto sale) return string.Empty;

        var userName = string.IsNullOrWhiteSpace(sale.ClaimedByUserName) ? "otro cajero" : sale.ClaimedByUserName;
        var actionLabel = sale.ClaimAction switch
        {
            "Checkout" => "En proceso de pago",
            "Editing" => "Editando pedido",
            _ => "Bloqueado"
        };

        return $"Bloqueado por {userName} - {actionLabel}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotImplementedException();
}
