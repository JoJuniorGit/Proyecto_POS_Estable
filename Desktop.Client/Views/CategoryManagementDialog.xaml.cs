using System;
using System.Windows;
using System.Windows.Input;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views
{
    public partial class CategoryManagementDialog : Window
    {
        public CategoryManagementDialogViewModel ViewModel { get; }

        private Action<bool>? _closeHandler;

        public CategoryManagementDialog(CategoryManagementDialogViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = ViewModel;

            // 8.179-T2 (S3): mismo contrato de cierre que RegisterSupplierDialog (delegado con
            // desuscripción en OnClosed, sin lambdas vivas); el resultado lleva si hubo cambios.
            _closeHandler = result =>
            {
                DialogResult = result;
            };
            ViewModel.RequestClose += _closeHandler;
        }

        private void CategoryManagementDialog_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                ViewModel.CloseCommand.Execute(null);
                e.Handled = true;
            }
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
