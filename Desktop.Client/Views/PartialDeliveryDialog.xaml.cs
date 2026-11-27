using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class PartialDeliveryDialog : Window
{
    public PartialDeliveryDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var sourceHeight = Owner?.ActualHeight > 0
            ? Owner.ActualHeight
            : SystemParameters.WorkArea.Height;
        MaxHeight = Math.Max(480d, sourceHeight * 0.85d);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(FocusFirstQuantity), DispatcherPriority.ApplicationIdle);
    }

    private void FocusFirstQuantity()
    {
        if (!IsLoaded) return;

        var textBox = FindVisualChild<TextBox>(this);
        if (textBox?.DataContext is not PartialDeliveryLineViewModel) return;

        textBox.Focus();
        Keyboard.Focus(textBox);
    }

    private void ConfirmButton_Click(object sender, RoutedEventArgs e) => TryConfirm();

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            TryConfirm();
            e.Handled = true;
        }
    }

    private void TryConfirm()
    {
        if (DataContext is not PartialDeliveryDialogViewModel viewModel || !viewModel.CanConfirm)
        {
            return;
        }

        viewModel.ConfirmCommand.Execute(null);
        if (viewModel.Result is not null)
        {
            DialogResult = true;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;

            var nested = FindVisualChild<T>(child);
            if (nested is not null) return nested;
        }

        return null;
    }
}
