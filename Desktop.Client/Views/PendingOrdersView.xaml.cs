using System;
using System.Windows;
using System.Windows.Controls;

namespace Desktop.Client.Views;

public partial class PendingOrdersView : UserControl
{
    public PendingOrdersView()
    {
        InitializeComponent();
    }

    private void PendingOrdersGrid_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is DataGrid grid && grid.Columns.Count >= 4)
        {
            grid.Columns[1].Width = new DataGridLength(
                Math.Max(260d, e.NewSize.Width - 165d - 185d - 155d - 8d),
                DataGridLengthUnitType.Pixel);
        }
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
