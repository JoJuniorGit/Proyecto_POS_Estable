using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
        DataContextChanged += LoginView_DataContextChanged;
    }

    private void LoginView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is LoginViewModel oldVm)
        {
            oldVm.PropertyChanged -= Vm_PropertyChanged;
        }
        if (e.NewValue is LoginViewModel newVm)
        {
            newVm.PropertyChanged += Vm_PropertyChanged;
        }
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is LoginViewModel vm && e.PropertyName == nameof(LoginViewModel.Password))
        {
            if (PasswordInput != null && PasswordInput.Password != (vm.Password ?? string.Empty))
            {
                PasswordInput.Password = vm.Password ?? string.Empty;
            }
        }
    }

    private void TextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (DataContext is LoginViewModel vm && vm.LoginCommand.CanExecute(null))
            {
                vm.LoginCommand.Execute(null);
            }
        }
    }

    private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm && sender is PasswordBox pb)
        {
            vm.Password = pb.Password;
        }
    }

    private void PasswordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (DataContext is LoginViewModel vm && vm.LoginCommand.CanExecute(null))
            {
                vm.LoginCommand.Execute(null);
            }
        }
    }
}
