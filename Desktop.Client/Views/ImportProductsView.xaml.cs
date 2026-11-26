using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Core.Common;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class ImportProductsView : UserControl
{
    private static readonly SolidColorBrush DropZoneIdleBackground = new(Color.FromRgb(0xF8, 0xFA, 0xFC));
    private static readonly SolidColorBrush DropZoneActiveBackground = new(Color.FromRgb(0xF5, 0xF3, 0xFF));
    private static readonly SolidColorBrush DropZoneIdleBorder = new(Color.FromRgb(0xCB, 0xD5, 0xE1));
    private static readonly SolidColorBrush DropZoneActiveBorder = new(Color.FromRgb(0x6D, 0x28, 0xD9));

    public ImportProductsView()
    {
        InitializeComponent();
    }

    private void Grid_DragOver(object sender, DragEventArgs e)
    {
        var supported = TryGetSupportedFile(e, out _);
        e.Effects = supported ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropZoneActive(supported);
        e.Handled = true;
    }

    private void Grid_DragLeave(object sender, DragEventArgs e)
    {
        SetDropZoneActive(false);
    }

    private void Grid_Drop(object sender, DragEventArgs e)
    {
        SetDropZoneActive(false);
        e.Handled = true;

        try
        {
            if (!TryGetSupportedFile(e, out var filePath))
            {
                return;
            }

            if (DataContext is ImportProductsViewModel viewModel &&
                viewModel.CanMutateCatalog &&
                !viewModel.IsBusy)
            {
                viewModel.ProcessFileAsync(filePath).SafeFireAndForget("ImportProductsView.Grid_Drop");
            }
        }
        catch (Exception ex)
        {
            Core.Logging.AppLogger.LogCrash(ex, "ImportProductsView.Grid_Drop");
        }
    }

    private static bool TryGetSupportedFile(DragEventArgs e, out string filePath)
    {
        filePath = string.Empty;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return false;
        }

        filePath = files.FirstOrDefault(IsSupportedFile) ?? string.Empty;
        return filePath.Length > 0;
    }

    private static bool IsSupportedFile(string filePath) =>
        filePath.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ||
        filePath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

    private void SetDropZoneActive(bool active)
    {
        if (DropZoneBorder is null || DropZoneDashedBorder is null)
        {
            return;
        }

        DropZoneBorder.Background = active ? DropZoneActiveBackground : DropZoneIdleBackground;
        DropZoneDashedBorder.Stroke = active ? DropZoneActiveBorder : DropZoneIdleBorder;
    }
}
