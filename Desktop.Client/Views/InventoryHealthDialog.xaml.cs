using Desktop.Client.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace Desktop.Client.Views;

public partial class InventoryHealthDialog : Window
{
    private readonly InventoryHealthViewModel _viewModel;

    public InventoryHealthDialog(InventoryHealthViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.RequestClose = Close;
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        await _viewModel.EnsureLoadedAsync();
    }

    private void InventoryHealthDialog_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }
}
