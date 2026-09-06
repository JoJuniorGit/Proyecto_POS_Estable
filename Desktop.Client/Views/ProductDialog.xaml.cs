using System.Windows;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views
{
    public partial class ProductDialog : Window
    {
        public ProductDialogViewModel ViewModel { get; }

        public ProductDialog(ProductDialogViewModel viewModel)
        {
            InitializeComponent();
            ViewModel = viewModel;
            DataContext = ViewModel;

            ViewModel.RequestClose += (bool result) =>
            {
                DialogResult = result;
            };
        }

        protected override void OnClosed(System.EventArgs e)
        {
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
