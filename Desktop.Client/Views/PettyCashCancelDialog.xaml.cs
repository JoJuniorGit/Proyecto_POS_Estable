using System;
using System.Windows;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class PettyCashCancelDialog : Window
{
    public PettyCashCancelDialog(PettyCashCancelDialogViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;
        ViewModel.RequestClose += OnRequestClose;
    }

    public PettyCashCancelDialogViewModel ViewModel { get; }

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
