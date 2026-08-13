using System;
using System.Windows;
using System.Windows.Controls;
using MaterialDesignThemes.Wpf;

namespace Desktop.Client.Views;

public partial class CashAmountDialog : UserControl
{
    public long Amount { get; private set; }
    private readonly long? _expectedAmount;

    public CashAmountDialog(string title, long? expectedAmount = null)
    {
        InitializeComponent();
        TitleText.Text = title;
        _expectedAmount = expectedAmount;
        
        if (_expectedAmount.HasValue)
        {
            BtnKeepAmount.Content = $"Keep expected cash ({_expectedAmount.Value} Bs.S)";
            BtnKeepAmount.Visibility = Visibility.Visible;
        }

        AmountBox.Focus();
    }

    private void BtnKeepAmount_Click(object sender, RoutedEventArgs e)
    {
        if (_expectedAmount.HasValue)
        {
            AmountBox.Text = _expectedAmount.Value.ToString();
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (long.TryParse(AmountBox.Text, out long amount) && amount >= 0)
        {
            Amount = amount;
            DialogHost.CloseDialogCommand.Execute(true, this);
        }
        else
        {
            ErrorText.Text = "Invalid amount.";
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
