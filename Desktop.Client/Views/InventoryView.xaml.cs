using System.Windows.Controls;

namespace Desktop.Client.Views
{
    public partial class InventoryView : UserControl
    {
        public InventoryView()
        {
            InitializeComponent();
        }

        private void SearchInput_GotFocus(object sender, System.Windows.RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                textBox.SelectionStart = 0;
                textBox.SelectionLength = textBox.Text.Length;
            }
        }
    }
}
