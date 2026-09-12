using System;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Desktop.Client.Views;

public partial class SalesHistoryView : UserControl
{
    private static readonly Regex NonDigitsRegex = new("[^0-9]+");

    public SalesHistoryView()
    {
        InitializeComponent();
        Loaded += SalesHistoryView_Loaded;
        Unloaded += SalesHistoryView_Unloaded;
    }

    private void TargetPageInput_GotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox textBox)
        {
            textBox.SelectAll();
        }
    }

    private void TargetPageInput_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBox textBox && !textBox.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            textBox.Focus();
            textBox.SelectAll();
        }
    }

    private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
    {
        e.Handled = NonDigitsRegex.IsMatch(e.Text);
    }

    private void SalesHistoryView_Loaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not Window window) return;
        window.PreviewMouseDown -= Window_PreviewMouseDown;
        window.PreviewKeyDown -= Window_PreviewKeyDown;
        window.PreviewMouseDown += Window_PreviewMouseDown;
        window.PreviewKeyDown += Window_PreviewKeyDown;
    }

    private void SalesHistoryView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is Window window)
        {
            window.PreviewMouseDown -= Window_PreviewMouseDown;
            window.PreviewKeyDown -= Window_PreviewKeyDown;
        }
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!SecondaryFiltersPopup.IsOpen) return;
        if (e.OriginalSource is not DependencyObject source) return;
        if (IsDescendantOf(source, FunnelButton)) return;
        SecondaryFiltersPopup.IsOpen = false;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SecondaryFiltersPopup.IsOpen)
        {
            SecondaryFiltersPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void FilterDraft_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && SecondaryFiltersPopup.IsOpen)
        {
            SecondaryFiltersPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void SecondaryFiltersPopup_Opened(object? sender, EventArgs e)
    {
        DraftCashierCombo.Focus();
    }

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        DependencyObject? current = child;
        while (current is not null)
        {
            if (current == ancestor) return true;
            current = current is Visual or Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }
}
