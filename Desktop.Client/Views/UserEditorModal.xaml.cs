using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class UserEditorModal : UserControl
{
    private UsersManagementViewModel? _viewModel;

    public UserEditorModal()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        IsVisibleChanged += OnIsVisibleChanged;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = e.NewValue as UsersManagementViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(UsersManagementViewModel.Password)) return;
        if (UserPasswordBox is null) return;

        var password = _viewModel?.Password ?? string.Empty;
        if (UserPasswordBox.Password != password)
        {
            UserPasswordBox.Password = password;
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_viewModel is null) return;

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _viewModel.CloseUserModalCommand.Execute(null);
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            _viewModel.SaveUserCommand.Execute(null);
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) return;

        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (UsernameTextBox is not null && UsernameTextBox.IsVisible)
                {
                    UsernameTextBox.Focus();
                    UsernameTextBox.SelectAll();
                }
            }));
    }

    private void UserPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && sender is PasswordBox passwordBox)
        {
            _viewModel.Password = passwordBox.Password;
        }
    }
}
