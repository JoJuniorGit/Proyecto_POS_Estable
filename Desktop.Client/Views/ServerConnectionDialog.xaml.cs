using System.Windows;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class ServerConnectionDialog : Window
{
    private readonly ServerConnectionViewModel _viewModel;

    public ServerConnectionDialog(ServerConnectionViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        _viewModel.RequestClose += success =>
        {
            DialogResult = success;
            Close();
        };
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
