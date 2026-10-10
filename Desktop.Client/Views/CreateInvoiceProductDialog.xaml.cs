using System;
using System.Windows;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views
{
    public partial class CreateInvoiceProductDialog : Window
    {
        public CreateInvoiceProductDialogViewModel ViewModel { get; }

        private Action<bool>? _closeHandler;

        public CreateInvoiceProductDialog(CreateInvoiceProductDialogViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = ViewModel;

            // 8.156 (PERF-04): delegado almacenado y desuscrito en OnClosed (sin lambda viva).
            _closeHandler = result =>
            {
                DialogResult = result;
            };
            ViewModel.RequestClose += _closeHandler;
        }

        protected override void OnClosed(EventArgs e)
        {
            if (_closeHandler != null)
            {
                ViewModel.RequestClose -= _closeHandler;
                _closeHandler = null;
            }

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
