using System.Windows;

namespace Desktop.Client.Views;

public partial class ChangePasswordDialog : Window
{
    public string CurrentPassword => CurrentPasswordBox.Password;
    public string NewPassword => NewPasswordBox.Password;

    public ChangePasswordDialog()
    {
        InitializeComponent();
        CurrentPasswordBox.Focus();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CurrentPasswordBox.Password))
        {
            ShowError("Ingrese su contraseña actual.");
            return;
        }

        if (string.IsNullOrWhiteSpace(NewPasswordBox.Password))
        {
            ShowError("Ingrese una nueva contraseña.");
            return;
        }

        if (NewPasswordBox.Password != ConfirmPasswordBox.Password)
        {
            ShowError("La nueva contraseña y su confirmación no coinciden.");
            return;
        }

        if (NewPasswordBox.Password.Length < 4)
        {
            ShowError("La nueva contraseña debe tener al menos 4 caracteres.");
            return;
        }

        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
