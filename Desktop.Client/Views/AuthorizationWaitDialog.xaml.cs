using System;
using System.Windows;
using System.Windows.Controls;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class AuthorizationWaitDialog : Window
{
    public AuthorizationWaitViewModel ViewModel { get; }

    public AuthorizationWaitDialog(AuthorizationWaitViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);

        InitializeComponent();
        ViewModel = viewModel;
        DataContext = ViewModel;
        ViewModel.RequestClose += OnRequestClose;
    }

    protected override void OnClosed(EventArgs e)
    {
        ViewModel.RequestClose -= OnRequestClose;
        ViewModel.Dispose();
        base.OnClosed(e);
    }

    private void OnRequestClose(bool result)
    {
        DialogResult = result;
    }

    private void LocalPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is AuthorizationWaitViewModel viewModel && sender is PasswordBox passwordBox)
        {
            viewModel.LocalPassword = passwordBox.Password;
        }
    }
}
