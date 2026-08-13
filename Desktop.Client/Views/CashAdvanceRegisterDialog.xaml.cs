using Desktop.Client.ViewModels;
using System.Windows;

namespace Desktop.Client.Views;

public partial class CashAdvanceRegisterDialog : Window
{
    public CashAdvanceRegisterViewModel ViewModel { get; }

    public CashAdvanceRegisterDialog(CashAdvanceRegisterViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;

        viewModel.CloseAction = () =>
        {
            try
            {
                DialogResult = viewModel.DialogResult;
            }
            catch
            {
                // Window might already be closing or non-modal
            }
            Close();
        };
        ConfigureOwner();
    }

    private void ConfigureOwner()
    {
        if (Application.Current != null && Application.Current.MainWindow != null && Application.Current.MainWindow.IsVisible)
        {
            Owner = Application.Current.MainWindow;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }
}
