using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class CreditReceivablesView : UserControl
{
    public CreditReceivablesView()
    {
        InitializeComponent();
    }

    private void MoreActionsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: not null } button)
        {
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            button.ContextMenu.IsOpen = true;
            e.Handled = true;
        }
    }

    private void View_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape
            && DataContext is CreditReceivablesViewModel viewModel
            && viewModel.IsTimelineOpen)
        {
            viewModel.CloseTimelineCommand.Execute(null);
            e.Handled = true;
        }
    }
}
