using System;
using System.Windows;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views
{
    public partial class CreateInvoiceProductDialog : Window
    {
        public CreateInvoiceProductDialogViewModel ViewModel { get; }

        public CreateInvoiceProductDialog(CreateInvoiceProductDialogViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = ViewModel;

            ViewModel.RequestClose += (bool result) =>
            {
                DialogResult = result;
            };
        }

        protected override void OnClosed(EventArgs e)
        {
            ViewModel?.Dispose();
            base.OnClosed(e);
        }

        /// <summary>8.146-T5 (S4): autofocus del campo EAN/UPC para captura directa con escáner.</summary>
        private void BarcodeTextBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.TextBox textBox)
            {
                textBox.Focus();
                textBox.SelectAll();
            }
        }
    }
}
