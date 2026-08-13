using System.Windows;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class VersionLockoutDialog : Window
{
    public VersionLockoutDialog(VersionLockoutViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
