using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views
{
    public partial class BulkPriceEditDialog : Window
    {
        public BulkPriceEditDialogViewModel ViewModel { get; }

        private Action<bool>? _closeHandler;

        public BulkPriceEditDialog(BulkPriceEditDialogViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = ViewModel;

            // 8.181-T2 (S5): mismo contrato de cierre que CategoryManagementDialog (delegado con
            // desuscripción en OnClosed, sin lambdas vivas); el resultado indica si se aplicó.
            _closeHandler = result =>
            {
                DialogResult = result;
            };
            ViewModel.RequestClose += _closeHandler;
        }

        private void BulkPriceEditDialog_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                ViewModel.CloseCommand.Execute(null);
                e.Handled = true;
            }
        }

        protected override void OnClosing(CancelEventArgs e)
        {
            // 8.181-T2 (fix verificación): bloquear el cierre por chrome (X) con una aplicación en
            // vuelo; el cierre exitoso se emite desde el VM con IsBusy ya en false. Null-safe: si el
            // ctor falló antes de asignar ViewModel (p.ej. XAML inválido), el cierre no debe crashear.
            if (ViewModel?.IsBusy == true)
            {
                e.Cancel = true;
            }

            base.OnClosing(e);
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
