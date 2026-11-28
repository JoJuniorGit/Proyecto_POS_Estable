using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class ProductPickerDialog : Window
{
    public ProductPickerDialogViewModel ViewModel { get; }

    private Action<bool>? _closeHandler;

    public ProductPickerDialog(ProductPickerDialogViewModel viewModel)
    {
        InitializeComponent();
        ArgumentNullException.ThrowIfNull(viewModel);
        ViewModel = viewModel;
        DataContext = ViewModel;

        _closeHandler = result =>
        {
            DialogResult = result;
        };
        ViewModel.RequestClose += _closeHandler;
    }

    private void ProductPickerDialog_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            ViewModel.CancelCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>
    /// WU3: la fila completa alterna la selección del producto. La casilla conserva su propio
    /// toggle nativo y no se intercepta; el resaltado es del ViewModel, no del ListBox.
    /// </summary>
    private void ResultsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        if (FindAncestor<CheckBox>(source) is not null)
        {
            return;
        }

        if (FindAncestor<ListBoxItem>(source) is { DataContext: ProductPickerRowViewModel row })
        {
            row.IsSelected = !row.IsSelected;
            e.Handled = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }

    protected override void OnClosed(EventArgs e)
    {
        if (_closeHandler is not null)
        {
            ViewModel.RequestClose -= _closeHandler;
            _closeHandler = null;
        }

        ViewModel.Dispose();
        base.OnClosed(e);
    }
}
