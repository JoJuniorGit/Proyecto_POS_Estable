using System.Windows;
using System.Windows.Input;
using System.Text.RegularExpressions;

namespace Desktop.Client.Views;

public partial class CashAdvanceDialog : Window
{
    public decimal RequestedAmountBsS { get; private set; }

    public CashAdvanceDialog()
    {
        InitializeComponent();
        AmountBox.Focus();
    }

    private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
    {
        Regex regex = new Regex("[^0-9.]+");
        e.Handled = regex.IsMatch(e.Text);
    }

    private void AmountBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetDataPresent(typeof(string)))
        {
            string text = (string)e.DataObject.GetData(typeof(string));
            if (new Regex("[^0-9.]+").IsMatch(text))
            {
                e.CancelCommand();
            }
        }
        else
        {
            e.CancelCommand();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (decimal.TryParse(AmountBox.Text, out decimal amount) && amount > 0)
        {
            RequestedAmountBsS = amount;
            DialogResult = true;
        }
        else
        {
            ErrorText.Text = "Please enter a valid amount greater than 0.";
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
