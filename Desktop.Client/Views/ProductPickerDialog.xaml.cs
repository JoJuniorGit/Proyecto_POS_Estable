using System;
using System.Windows;
using System.Windows.Input;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class ProductPickerDialog : Window
{
    public ProductPickerDialogViewModel ViewModel { get; }

    private Action<bool>? _closeHandler;

    public ProductPickerDialog(ProductPickerDialogViewModel viewModel)
    {
        InitializeComponent();
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        DataContext = ViewModel;

        _closeHandler = result =>
        {
            DialogResult = result;
        };
        ViewModel.RequestClose += _closeHandler;
    }

    private void ProductPickerDialog_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ViewModel.CancelCommand.Execute(null);
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_closeHandler is not null)
        {
            ViewModel.RequestClose -= _closeHandler;
            _closeHandler = null;
        }

        ViewModel.Dispose();
        base.OnClosed(e);
    }
}
