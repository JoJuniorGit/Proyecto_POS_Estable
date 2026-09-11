using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Desktop.Client.Views;

public partial class SalesHistoryView : UserControl
{
    private static readonly Regex NonDigitsRegex = new("[^0-9]+");

    public SalesHistoryView()
    {
        InitializeComponent();
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
}
