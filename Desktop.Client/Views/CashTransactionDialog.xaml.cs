using System.Windows;
using System.Windows.Controls;
using MaterialDesignThemes.Wpf;

namespace Desktop.Client.Views;

public partial class CashTransactionDialog : UserControl
{
    public long Amount { get; private set; }
    public string Reason { get; private set; } = string.Empty;

    public CashTransactionDialog(string title)
    {
        InitializeComponent();
        TitleText.Text = title;
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(AmountBox.Text))
        {
            ShowError("Please enter a valid amount.");
            return;
        }

        if (!long.TryParse(AmountBox.Text, out long amount) || amount <= 0)
        {
            ShowError("Amount must be a positive number greater than zero.");
            return;
        }

        if (string.IsNullOrWhiteSpace(ReasonBox.Text))
        {
            ShowError("Please provide a reason or description.");
            ReasonBox.Focus();
            return;
        }

        Amount = amount;
        string reasonText = ReasonBox.Text.Trim();
        if (reasonText.Length > 40)
        {
            reasonText = reasonText.Substring(0, 40).Trim();
        }
        Reason = reasonText;
        
        DialogHost.CloseDialogCommand.Execute(true, this);
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
