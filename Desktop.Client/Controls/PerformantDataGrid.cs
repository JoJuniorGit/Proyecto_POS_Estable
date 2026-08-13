using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace Desktop.Client.Controls;

/// <summary>
/// A DataGrid subclass that disables the WPF UI Automation peer tree.
/// 
/// WPF creates AutomationPeer objects for every row and cell in a DataGrid
/// to support accessibility tools (screen readers, etc.). For large datasets,
/// this recursive tree walk can trigger OutOfMemoryException — confirmed in
/// crash.txt on 2026-04-25 at DataGrid.OnCreateAutomationPeer().
///
/// Returning null prevents the automation tree from being created, which
/// eliminates the OOM crash at the cost of reduced screen reader support.
/// </summary>
public class PerformantDataGrid : DataGrid
{
    protected override AutomationPeer? OnCreateAutomationPeer() => null;
}
