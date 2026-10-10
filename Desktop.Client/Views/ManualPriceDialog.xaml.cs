using System.Windows;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class ManualPriceDialog : Window
{
    public ManualPriceDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => UsdInput.Focus();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ManualPriceDialogViewModel vm) return;

        vm.AcceptCommand.Execute(null);
        if (vm.Result is null) return;

        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
