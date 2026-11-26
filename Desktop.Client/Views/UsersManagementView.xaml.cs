using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Core.DTOs;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class UsersManagementView : UserControl
{
    public UsersManagementView()
    {
        InitializeComponent();
    }

    private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
    {
        e.Handled = e.Text.Any(character => !char.IsDigit(character));
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

    private void UsersGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (DataContext is not UsersManagementViewModel vm) return;
        if (sender is DataGrid { SelectedItem: UserDto user })
        {
            vm.OpenEditUserCommand.Execute(user);
        }
    }

    private void CustomersGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (DataContext is not UsersManagementViewModel vm) return;
        if (sender is DataGrid { SelectedItem: CustomerDto customer })
        {
            vm.CustomerViewModel.OpenEditCustomerCommand.Execute(customer);
        }
    }
}
