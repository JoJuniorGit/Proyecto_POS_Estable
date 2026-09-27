using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Desktop.Client.ViewModels;

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
        window.PreviewKeyDown -= Window_PreviewKeyDown;
        window.PreviewKeyDown += Window_PreviewKeyDown;
    }

    private void SalesHistoryView_Unloaded(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is Window window)
        {
            window.PreviewKeyDown -= Window_PreviewKeyDown;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not SalesHistoryViewModel vm) return;
        if (!vm.IsFilterFlyoutOpen) return;
        vm.CloseFilterFlyoutCommand.Execute(null);
        e.Handled = true;
    }

    private void FilterFlyoutPanel_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            Dispatcher.BeginInvoke(() => DraftCashierCombo.Focus());
        }
    }
}
