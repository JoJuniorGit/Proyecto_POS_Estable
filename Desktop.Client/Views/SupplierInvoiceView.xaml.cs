using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Core.Common;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class SupplierInvoiceView : UserControl
{
    public SupplierInvoiceView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    /// <summary>8.161: al abrir la vista se carga el listado completo de proveedores del selector.</summary>
    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is SupplierInvoiceViewModel viewModel)
        {
            viewModel.LoadSuppliersCommand.ExecuteAsync(null)
                .SafeFireAndForget("SupplierInvoiceView.LoadSuppliers");
        }
    }

    private void UserControl_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void UserControl_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Length > 0 &&
            DataContext is SupplierInvoiceViewModel vm)
        {
            vm.ProcessFileAsync(files[0]).SafeFireAndForget("SupplierInvoiceView.Drop");
        }
    }

    private void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image != null && DataContext is SupplierInvoiceViewModel vm)
                {
                    try
                    {
                        var tempPath = Path.Combine(Path.GetTempPath(), $"factura_clipboard_{Guid.NewGuid():N}.png");
                        var encoder = new PngBitmapEncoder();
                        encoder.Frames.Add(BitmapFrame.Create(image));
                        using (var stream = File.Create(tempPath))
                        {
                            encoder.Save(stream);
                        }

                        vm.ProcessFileAsync(tempPath).SafeFireAndForget("SupplierInvoiceView.PasteImage");
                        e.Handled = true;
                    }
                    catch
                    {
                        // Evita propagar excepciones si el portapapeles o el sistema de archivos no responde.
                    }
                }
            }
            else if (Clipboard.ContainsFileDropList())
            {
                try
                {
                    var fileDropList = Clipboard.GetFileDropList();
                    if (fileDropList != null && fileDropList.Count > 0 && DataContext is SupplierInvoiceViewModel vm)
                    {
                        var filePath = fileDropList[0];
                        if (!string.IsNullOrWhiteSpace(filePath))
                        {
                            vm.ProcessFileAsync(filePath).SafeFireAndForget("SupplierInvoiceView.PasteFile");
                            e.Handled = true;
                        }
                    }
                }
                catch
                {
                    // Evita propagar excepciones si el portapapeles no responde.
                }
            }
        }
    }
}
