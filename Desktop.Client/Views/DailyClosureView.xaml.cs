using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Core.Helpers;
using Desktop.Client.Services;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class DailyClosureView : UserControl
{
    public DailyClosureView()
    {
        InitializeComponent();
    }

    private void AmountInput_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox textBox) return;

        // Select the current amount so typing replaces it instead of appending cents.
        textBox.Dispatcher.BeginInvoke(new Action(textBox.SelectAll), DispatcherPriority.Input);
    }

    private void AmountInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox textBox) return;

        // Keep the operator's flow: Enter advances to the next amount field.
        textBox.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        e.Handled = true;
    }

    private void UsdInput_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not TextBox textBox || DataContext is not DailyClosureViewModel viewModel) return;

        var raw = textBox.Text;
        if (string.IsNullOrWhiteSpace(raw))
        {
            viewModel.ActualClosingBalanceUsdText = string.Empty;
            return;
        }

        if (!FlexibleDecimalParser.TryParse(raw, out var parsed)) return;

        var formatted = CurrencyDisplay.Number(parsed);
        textBox.Text = formatted;
        textBox.CaretIndex = formatted.Length;
        viewModel.ActualClosingBalanceUsdText = formatted;
    }
}
