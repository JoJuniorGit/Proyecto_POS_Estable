using System.Windows;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class VariantManagementDialog : Window
{
    public VariantManagementViewModel ViewModel { get; }

    public VariantManagementDialog(VariantManagementViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        ViewModel.RequestClose = (result) =>
        {
            DialogResult = result;
            Close();
        };
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
