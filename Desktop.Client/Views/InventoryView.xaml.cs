using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views
{
    public partial class InventoryView : UserControl
    {
        private ScrollViewer? _dataGridScrollViewer;

        public InventoryView()
        {
            InitializeComponent();
            DataContextChanged += InventoryView_DataContextChanged;
        }

        private void InventoryView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is InventoryViewModel oldVm)
            {
                oldVm.PropertyChanged -= ViewModel_PropertyChanged;
            }
            if (e.NewValue is InventoryViewModel newVm)
            {
                newVm.PropertyChanged += ViewModel_PropertyChanged;
            }
        }

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(InventoryViewModel.CurrentPage) || e.PropertyName == nameof(InventoryViewModel.PageSummary))
            {
                Dispatcher.InvokeAsync(ScrollToTop, System.Windows.Threading.DispatcherPriority.Loaded);
            }
        }

        private void ScrollToTop()
        {
            if (_dataGridScrollViewer == null && ProductsDataGrid != null)
            {
                _dataGridScrollViewer = FindVisualChild<ScrollViewer>(ProductsDataGrid);
            }
            _dataGridScrollViewer?.ScrollToTop();
            if (ProductsDataGrid?.Items.Count > 0)
            {
                ProductsDataGrid.ScrollIntoView(ProductsDataGrid.Items[0]);
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild)
                    return typedChild;
                var childOfChild = FindVisualChild<T>(child);
                if (childOfChild != null)
                    return childOfChild;
            }
            return null;
        }

        private void SearchInput_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                textBox.SelectionStart = 0;
                textBox.SelectionLength = textBox.Text.Length;
            }
        }

        private void TargetPageInput_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                textBox.SelectAll();
            }
        }

        private void TargetPageInput_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is TextBox textBox && !textBox.IsKeyboardFocusWithin)
            {
                e.Handled = true;
                textBox.Focus();
                textBox.SelectAll();
            }
        }

        private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
        {
            System.Text.RegularExpressions.Regex regex = new System.Text.RegularExpressions.Regex("[^0-9]+");
            e.Handled = regex.IsMatch(e.Text);
        }
    }
}
