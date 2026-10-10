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
        private InventoryViewModel? _boundViewModel;

        public InventoryView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            HookViewModel(DataContext as InventoryViewModel);
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            UnhookViewModel();
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.OldValue is InventoryViewModel oldVm)
            {
                if (ReferenceEquals(_boundViewModel, oldVm))
                {
                    UnhookViewModel();
                }
            }

            if (e.NewValue is InventoryViewModel newVm)
            {
                HookViewModel(newVm);
            }
        }

        private void HookViewModel(InventoryViewModel? vm)
        {
            if (vm == null || ReferenceEquals(_boundViewModel, vm)) return;

            UnhookViewModel();
            _boundViewModel = vm;
            _boundViewModel.PropertyChanged += ViewModel_PropertyChanged;
        }

        private void UnhookViewModel()
        {
            if (_boundViewModel != null)
            {
                _boundViewModel.PropertyChanged -= ViewModel_PropertyChanged;
                _boundViewModel = null;
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

        private static readonly System.Text.RegularExpressions.Regex NonDigitsRegex = new("[^0-9]+");

        private void NumberValidationTextBox(object sender, TextCompositionEventArgs e)
        {
            e.Handled = NonDigitsRegex.IsMatch(e.Text);
        }

        private void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                if (DataContext is InventoryViewModel vm && vm.RefreshCommand.CanExecute(null))
                {
                    e.Handled = true;
                    vm.RefreshCommand.Execute(null);
                }
            }
        }
    }
}
