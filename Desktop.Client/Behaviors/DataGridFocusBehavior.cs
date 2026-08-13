using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;

namespace Desktop.Client.Behaviors;

/// <summary>
/// Attached behavior that ensures keyboard focus follows DataGrid selection.
/// When SelectedItem changes (e.g. after collection refresh), this behavior
/// uses Dispatcher to push keyboard focus into the selected DataGridRow,
/// ensuring InputBindings (like +/-) remain responsive.
/// </summary>
public static class DataGridFocusBehavior
{
    public static readonly DependencyProperty KeepFocusOnSelectionProperty =
        DependencyProperty.RegisterAttached(
            "KeepFocusOnSelection",
            typeof(bool),
            typeof(DataGridFocusBehavior),
            new PropertyMetadata(false, OnKeepFocusOnSelectionChanged));

    public static bool GetKeepFocusOnSelection(DependencyObject obj) =>
        (bool)obj.GetValue(KeepFocusOnSelectionProperty);

    public static void SetKeepFocusOnSelection(DependencyObject obj, bool value) =>
        obj.SetValue(KeepFocusOnSelectionProperty, value);

    private static void OnKeepFocusOnSelectionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DataGrid dg)
        {
            if ((bool)e.NewValue)
                dg.SelectionChanged += DataGrid_SelectionChanged;
            else
                dg.SelectionChanged -= DataGrid_SelectionChanged;
        }
    }

    private static void DataGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not DataGrid dg) return;
        if (dg.SelectedItem == null) return;

        // Use Input priority so the visual tree has finished updating
        dg.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            dg.UpdateLayout();

            // Scroll the selected item into view first
            dg.ScrollIntoView(dg.SelectedItem);

            // Get the DataGridRow container for the selected item
            var row = dg.ItemContainerGenerator.ContainerFromItem(dg.SelectedItem) as DataGridRow;
            if (row != null)
            {
                row.MoveFocus(new System.Windows.Input.TraversalRequest(
                    System.Windows.Input.FocusNavigationDirection.First));
            }
            else
            {
                // Fallback: at least keep keyboard focus on the DataGrid itself
                dg.Focus();
            }
        });
    }
}
