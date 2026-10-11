using System.Windows;
using System.Windows.Controls;
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
}
