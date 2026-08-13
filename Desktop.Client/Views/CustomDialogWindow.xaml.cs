using System;
using System.Windows;
using System.Windows.Media;
using MaterialDesignThemes.Wpf;

namespace Desktop.Client.Views;

public enum CustomDialogType
{
    Confirm,
    Info,
    Warning,
    Error
}

public partial class CustomDialogWindow : Window
{
    public CustomDialogWindow(string title, string message, CustomDialogType dialogType)
    {
        InitializeComponent();
        DataContext = new { Title = title, Message = message };

        ConfigureWindowOwner();
        ConfigureDialog(dialogType);
    }

    private void ConfigureWindowOwner()
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

    private void ConfigureDialog(CustomDialogType dialogType)
    {
        switch (dialogType)
        {
            case CustomDialogType.Confirm:
                DialogIcon.Kind = PackIconKind.HelpCircleOutline;
                DialogIcon.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#3B82F6"));
                BtnYes.Content = "Sí";
                BtnNo.Visibility = Visibility.Visible;
                break;

            case CustomDialogType.Info:
                DialogIcon.Kind = PackIconKind.InformationOutline;
                DialogIcon.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#10B981"));
                BtnYes.Content = "Aceptar";
                BtnNo.Visibility = Visibility.Collapsed;
                break;

            case CustomDialogType.Warning:
                DialogIcon.Kind = PackIconKind.AlertCircleOutline;
                DialogIcon.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F59E0B"));
                BtnYes.Content = "Aceptar";
                BtnNo.Visibility = Visibility.Collapsed;
                break;

            case CustomDialogType.Error:
                DialogIcon.Kind = PackIconKind.CloseCircleOutline;
                DialogIcon.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EF4444"));
                BtnYes.Content = "Aceptar";
                BtnNo.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void BtnYes_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void BtnNo_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
