using System.Windows.Controls;
using System.Windows.Input;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class CreditPaymentDialog : UserControl
{
    public CreditPaymentDialog()
    {
        InitializeComponent();
    }

    private void Dialog_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not CreditPaymentDialogViewModel vm)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (!vm.IsProcessing && vm.CloseCommand.CanExecute(null))
            {
                vm.CloseCommand.Execute(null);
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            if (vm.CanConfirm && vm.ConfirmCommand.CanExecute(null))
            {
                vm.ConfirmCommand.Execute(null);
                e.Handled = true;
            }
        }
    }

    private void AmountInput_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox textBox)
        {
            return;
        }

        if (!textBox.IsKeyboardFocusWithin)
        {
            textBox.Focus();
        }

        textBox.SelectAll();
        e.Handled = true;
    }
}
