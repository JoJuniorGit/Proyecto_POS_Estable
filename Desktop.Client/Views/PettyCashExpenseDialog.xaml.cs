using System;
using System.Windows;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class PettyCashExpenseDialog : Window
{
    public PettyCashExpenseDialog(PettyCashExpenseDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;
        ViewModel.RequestClose += OnRequestClose;
    }

    public PettyCashExpenseDialogViewModel ViewModel { get; }

    private void OnRequestClose(bool _)
    {
        if (IsVisible)
        {
            Close();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.RequestClose -= OnRequestClose;
        ViewModel.Dispose();
        base.OnClosed(e);
    }
}
