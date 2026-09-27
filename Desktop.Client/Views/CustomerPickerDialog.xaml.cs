using Core.DTOs;
using Desktop.Client.ViewModels;
using System;
using System.Windows;

namespace Desktop.Client.Views;

public partial class CustomerPickerDialog : Window
{
    public CustomerPickerViewModel ViewModel { get; }
    public CustomerDto? SelectedCustomer { get; private set; }

    public CustomerPickerDialog(CustomerPickerViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
        Closed += (s, e) => (DataContext as IDisposable)?.Dispose();
    }

    private void SearchTab_Click(object sender, RoutedEventArgs e)
        => ViewModel.SwitchToSearch();

    private void CreateTab_Click(object sender, RoutedEventArgs e)
        => ViewModel.SwitchToCreate();

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedCustomer == null) return;
        SelectedCustomer = ViewModel.SelectedCustomer;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
