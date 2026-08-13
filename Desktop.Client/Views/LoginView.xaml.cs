using System.Windows.Controls;
using System.Windows.Input;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
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
}
