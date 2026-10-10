using System;
using System.Windows;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views
{
    public partial class ProductDialog : Window
    {
        private Action<bool>? _closeHandler;

        public ProductDialogViewModel ViewModel { get; }

        public ProductDialog(ProductDialogViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = ViewModel;

            _closeHandler = result =>
            {
                DialogResult = result;
            };
            ViewModel.RequestClose += _closeHandler;
        }

        protected override void OnClosed(System.EventArgs e)
        {
            // PERF-04 (8.156): la lambda anterior capturaba `this` y quedaba viva en el VM;
            // se guarda la referencia para desuscribirla antes de disponer el ViewModel.
            if (_closeHandler != null)
            {
                ViewModel.RequestClose -= _closeHandler;
                _closeHandler = null;
            }

            ViewModel?.Dispose();
            base.OnClosed(e);
        }

        private void NumberValidationTextBox(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            if (ViewModel?.IsGroupHeader == true) return;
            // Permite caracteres alfanuméricos, guiones y guiones bajos para SKU/Código de barras
            System.Text.RegularExpressions.Regex regex = new System.Text.RegularExpressions.Regex(@"[^a-zA-Z0-9\-_]+");
            e.Handled = regex.IsMatch(e.Text);
        }
    }
}
