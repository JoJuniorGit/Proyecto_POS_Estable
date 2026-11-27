using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Core.Entities;
using Desktop.Client.Navigation;

namespace Desktop.Client.Converters;

/// <summary>Shows a sidebar item only when the current role can access the requested view.</summary>
public sealed class RoleViewVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is UserRole role && parameter is string view)
        {
            return WpfNavigationPolicy.CanAccess(role, view)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Shows a category header only when at least one item in it is allowed for the role.</summary>
public sealed class RoleCategoryVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is UserRole role
            && parameter is string categoryName
            && Enum.TryParse<WpfNavigationCategory>(categoryName, ignoreCase: true, out var category))
        {
            return WpfNavigationPolicy.CanAccessCategory(role, category)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
