using System.Windows;
using System.Windows.Controls;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class ImportProductsView : UserControl
{
    public ImportProductsView()
    {
        InitializeComponent();
    }

    private async void Grid_Drop(object sender, DragEventArgs e)
    {
        try
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                string[] files = (string[])e.Data.GetData(DataFormats.FileDrop);
                if (files is { Length: > 0 } && files[0].EndsWith(".xlsx", System.StringComparison.OrdinalIgnoreCase))
                {
                    if (DataContext is ImportProductsViewModel vm)
                    {
                        await vm.ProcessFileAsync(files[0]);
                    }
                }
            }
        }
        catch (System.Exception ex)
        {
            Core.Logging.AppLogger.LogCrash(ex, "ImportProductsView.Grid_Drop");
        }
    }
}
