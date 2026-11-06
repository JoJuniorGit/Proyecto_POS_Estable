using System;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Desktop.Client.Services;

namespace Desktop.Client.Services;

/// <summary>
/// 8.211-T6-U12b: WPF amount converters read the static CurrencyDisplay at conversion time. When
/// the profile changes, re-evaluate every active binding expression in every open window so labels
/// and amounts are refreshed without recreating the views. The presenter handles the bindings that
/// target it directly; this refresher covers DataContext-bound amounts and the remaining legacy
/// bindings.
/// </summary>
public static class CurrencyBindingRefresher
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed)
        {
            return;
        }

        _installed = true;
        CurrencyDisplay.ProfileChanged += OnProfileChanged;
    }

    private static void OnProfileChanged()
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        app.Dispatcher.BeginInvoke(new Action(RefreshAllWindows));
    }

    private static void RefreshAllWindows()
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        foreach (Window window in app.Windows)
        {
            RefreshTree(window);
        }
    }

    private static void RefreshTree(DependencyObject node)
    {
        var values = node.GetLocalValueEnumerator();
        while (values.MoveNext())
        {
            var expression = BindingOperations.GetBindingExpressionBase(node, values.Current.Property);
            if (expression is null)
            {
                continue;
            }

            try
            {
                expression.UpdateTarget();
            }
            catch (InvalidOperationException)
            {
                // A binding can be detached while the visual tree is being refreshed.
            }
        }

        var children = VisualTreeHelper.GetChildrenCount(node);
        for (var index = 0; index < children; index++)
        {
            RefreshTree(VisualTreeHelper.GetChild(node, index));
        }
    }
}
