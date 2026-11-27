using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class CustomerEditorModal : UserControl
{
    public CustomerEditorModal()
    {
        InitializeComponent();
        IsVisibleChanged += OnIsVisibleChanged;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not CustomerManagementViewModel vm) return;

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            vm.CloseCustomerModalCommand.Execute(null);
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            e.Handled = true;
            vm.SaveCustomerCommand.Execute(null);
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!IsVisible) return;

        Dispatcher.BeginInvoke(
            DispatcherPriority.Input,
            new Action(() =>
            {
                if (CedulaOrRifTextBox is not null && CedulaOrRifTextBox.IsVisible)
                {
                    CedulaOrRifTextBox.Focus();
                    CedulaOrRifTextBox.SelectAll();
                }
            }));
    }
}
