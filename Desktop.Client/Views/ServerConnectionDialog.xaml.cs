using System;
using System.Windows;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class ServerConnectionDialog : Window
{
    private readonly ServerConnectionViewModel _viewModel;
    private Action<bool>? _closeHandler;

    public ServerConnectionDialog(ServerConnectionViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        _closeHandler = success =>
        {
            DialogResult = success;
            Close();
        };
        _viewModel.RequestClose += _closeHandler;
    }

    protected override void OnClosed(EventArgs e)
    {
        // PERF-04 (8.156): desuscribe el handler antes de disponer el ViewModel para no
        // retener el dialogo cerrado a traves del delegado.
        if (_closeHandler != null)
        {
            _viewModel.RequestClose -= _closeHandler;
            _closeHandler = null;
        }

        (DataContext as System.IDisposable)?.Dispose();
        base.OnClosed(e);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
