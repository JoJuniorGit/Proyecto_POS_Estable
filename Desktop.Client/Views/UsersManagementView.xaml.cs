using System.Windows;
using System.Windows.Controls;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class UsersManagementView : UserControl
{
    public UsersManagementView()
    {
        InitializeComponent();
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
