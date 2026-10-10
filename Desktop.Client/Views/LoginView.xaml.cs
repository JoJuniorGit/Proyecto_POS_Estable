using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class LoginView : UserControl
{
    // PERF-05 (8.156): LoginView no dispone el LoginViewModel (singleton de DI, 8.149-W11);
    // solo se desenganchan los eventos al desalojar la vista.
    private LoginViewModel? _boundViewModel;

    public LoginView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        HookViewModel(DataContext as LoginViewModel);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        UnhookViewModel();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is LoginViewModel oldVm)
        {
            if (ReferenceEquals(_boundViewModel, oldVm))
            {
                UnhookViewModel();
            }
        }

        if (e.NewValue is LoginViewModel newVm)
        {
            HookViewModel(newVm);
        }
    }

    private void HookViewModel(LoginViewModel? vm)
    {
        if (vm == null || ReferenceEquals(_boundViewModel, vm)) return;

        UnhookViewModel();
        _boundViewModel = vm;
        _boundViewModel.PropertyChanged += Vm_PropertyChanged;
    }

    private void UnhookViewModel()
    {
        if (_boundViewModel != null)
        {
            _boundViewModel.PropertyChanged -= Vm_PropertyChanged;
            _boundViewModel = null;
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
