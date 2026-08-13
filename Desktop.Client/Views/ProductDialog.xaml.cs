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

        private void NumberValidationTextBox(object sender, System.Windows.Input.TextCompositionEventArgs e)
        {
            System.Text.RegularExpressions.Regex regex = new System.Text.RegularExpressions.Regex("[^0-9]+");
            e.Handled = regex.IsMatch(e.Text);
        }
    }
}
