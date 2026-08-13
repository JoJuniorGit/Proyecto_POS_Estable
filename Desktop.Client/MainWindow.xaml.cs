using Desktop.Client.ViewModels;
using System.Windows;

namespace Desktop.Client;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}