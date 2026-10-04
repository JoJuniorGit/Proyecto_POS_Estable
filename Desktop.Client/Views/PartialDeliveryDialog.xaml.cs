using System.Windows;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class PartialDeliveryDialog : Window
{
    public PartialDeliveryDialog()
    {
        InitializeComponent();
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PartialDeliveryDialogViewModel vm || !vm.CanConfirm) return;

        vm.ConfirmCommand.Execute(null);
        DialogResult = vm.Result is not null;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
