using Desktop.Client.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace Desktop.Client.Views;

public partial class VariantSelectionDialog : Window
{
    private readonly VariantSelectionViewModel _viewModel;

    public VariantSelectionDialog(VariantSelectionViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        Owner = Application.Current?.MainWindow;
        _viewModel.RequestClose = (result) =>
        {
            DialogResult = result;
            Close();
        };

        Loaded += (s, e) =>
        {
            VariantsListBox.Focus();
        };
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _viewModel.CancelCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            _viewModel.SelectVariantCommand.Execute(_viewModel.CurrentSelectedVariant);
            e.Handled = true;
        }
    }

    private void ListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.CurrentSelectedVariant != null)
        {
            _viewModel.SelectVariantCommand.Execute(_viewModel.CurrentSelectedVariant);
        }
    }

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectVariantCommand.Execute(_viewModel.CurrentSelectedVariant);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.CancelCommand.Execute(null);
    }
}
