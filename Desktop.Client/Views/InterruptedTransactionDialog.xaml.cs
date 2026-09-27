using System.Windows;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class InterruptedTransactionDialog : Window
{
    public InterruptedTransactionDialog(InterruptedTransactionViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.RequestClose += OnRequestClose;
        Closed += (_, _) => viewModel.RequestClose -= OnRequestClose;
    }

    private void OnRequestClose() => Close();
}
