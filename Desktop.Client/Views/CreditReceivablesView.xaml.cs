using System.Windows;
using System.Windows.Controls;

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
}
