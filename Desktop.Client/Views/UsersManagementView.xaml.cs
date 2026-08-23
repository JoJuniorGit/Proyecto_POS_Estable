using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class UsersManagementView : UserControl
{
    public UsersManagementView()
    {
        InitializeComponent();
        DataContextChanged += UsersManagementView_DataContextChanged;
    }

    private void UsersManagementView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is UsersManagementViewModel oldVm)
        {
            oldVm.PropertyChanged -= Vm_PropertyChanged;
        }
        if (e.NewValue is UsersManagementViewModel newVm)
        {
            newVm.PropertyChanged += Vm_PropertyChanged;
        }
    }

    private void Vm_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UsersManagementViewModel.Password) && sender is UsersManagementViewModel vm)
        {
            if (string.IsNullOrEmpty(vm.Password) && UserPasswordBox != null && UserPasswordBox.Password != string.Empty)
            {
                UserPasswordBox.Password = string.Empty;
            }
        }
    }

    private void UserPasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is UsersManagementViewModel vm && sender is PasswordBox pb)
        {
            vm.Password = pb.Password;
        }
    }

    private void TabUsers_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is UsersManagementViewModel vm)
        {
            vm.SelectedTabIndex = 0;
        }
    }

    private void TabCustomers_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is UsersManagementViewModel vm)
        {
            vm.SelectedTabIndex = 1;
        }
    }
}
