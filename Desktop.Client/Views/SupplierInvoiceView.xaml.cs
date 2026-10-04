using System.Windows.Controls;
using System.Windows.Input;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class SupplierInvoiceView : UserControl
{
    public SupplierInvoiceView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 8.147-T9/D5c: la spec de revisión admite zoom por rueda o botones; la rueda se consume
    /// para escalar el preview en vez de desplazarlo.
    /// </summary>
    private void OcrPreview_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (DataContext is not SupplierInvoiceViewModel viewModel)
        {
            return;
        }

        if (e.Delta > 0)
        {
            viewModel.OcrZoomInCommand.Execute(null);
        }
        else if (e.Delta < 0)
        {
            viewModel.OcrZoomOutCommand.Execute(null);
        }

        e.Handled = true;
    }
}
