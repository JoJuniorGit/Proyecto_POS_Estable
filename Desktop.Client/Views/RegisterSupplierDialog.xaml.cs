using System;
using System.Windows;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views
{
    public partial class RegisterSupplierDialog : Window
    {
        public RegisterSupplierDialogViewModel ViewModel { get; }

        private Action<bool>? _closeHandler;

        public RegisterSupplierDialog(RegisterSupplierDialogViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = ViewModel;

            // 8.161: mismo contrato de cierre que CreateInvoiceProductDialog (delegado con
            // desuscripcion en OnClosed, sin lambdas vivas).
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
    }
}
