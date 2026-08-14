using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Desktop.Client.Views
{
    public partial class PosView : UserControl
    {
        private BarcodeScannerWindow? _scannerWindow;

        public PosView()
        {
            InitializeComponent();
        }

        private void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F3)
            {
                e.Handled = true;
                FocusSearch();
            }
        }

        private void FocusSearch_Click(object sender, RoutedEventArgs e)
        {
            FocusSearch();
        }

        private void FocusSearch()
        {
            SearchInput.Focus();
        }

        /// <summary>
        /// Opens the floating barcode scanner / OCR window. The window stays open so the
        /// cashier can scan several codes; each scanned barcode is added straight to the cart,
        /// and the window shows the product name (or not-found/inactive states) on its result card.
        /// </summary>
        private void OpenScanner_Click(object sender, RoutedEventArgs e)
        {
            if (_scannerWindow != null && _scannerWindow.IsVisible)
            {
                _scannerWindow.Activate();
                return;
            }

            _scannerWindow = new BarcodeScannerWindow(InsertScannedValue, ResolveScannedProductAsync)
            {
                Owner = Window.GetWindow(this)
            };
            _scannerWindow.Show();
        }

        private void InsertScannedValue(string value)
        {
            if (DataContext is ViewModels.PosViewModel vm)
            {
                _ = vm.AddProductByCodeAsync(value);
                SearchInput.Focus();
            }
        }

        private Task<Core.DTOs.ProductQuickInfoDto?> ResolveScannedProductAsync(string code)
        {
            if (DataContext is ViewModels.PosViewModel vm)
            {
                return vm.ResolveScannedCodeAsync(code);
            }
            return Task.FromResult<Core.DTOs.ProductQuickInfoDto?>(null);
        }



        /// <summary>
        /// Auto-focus and select-all when the Quantity editing TextBox appears (double-click).
        /// Wires PreviewTextInput and DataObject.Pasting handlers for strict quantity validation.
        /// </summary>
        private void EditQuantityBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox tb)
            {
                tb.Focus();
                tb.SelectAll();

                tb.PreviewTextInput -= EditQuantityBox_PreviewTextInput;
                tb.PreviewTextInput += EditQuantityBox_PreviewTextInput;

                DataObject.RemovePastingHandler(tb, EditQuantityBox_Pasting);
                DataObject.AddPastingHandler(tb, EditQuantityBox_Pasting);
            }
        }

        private void EditQuantityBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is ViewModels.CartItemViewModel itemVm)
            {
                string proposedText = GetProposedText(tb, e.Text);
                if (!IsQuantityTextValid(proposedText, itemVm.Model.IsFractional))
                {
                    e.Handled = true;
                }
            }
        }

        private void EditQuantityBox_Pasting(object sender, DataObjectPastingEventArgs e)
        {
            if (sender is TextBox tb && tb.DataContext is ViewModels.CartItemViewModel itemVm)
            {
                if (e.DataObject.GetDataPresent(typeof(string)))
                {
                    string pasteText = e.DataObject.GetData(typeof(string)) as string ?? string.Empty;
                    string proposedText = GetProposedText(tb, pasteText);

                    if (!IsQuantityTextValid(proposedText, itemVm.Model.IsFractional))
                    {
                        e.CancelCommand();
                        e.Handled = true;
                    }
                }
                else
                {
                    e.CancelCommand();
                    e.Handled = true;
                }
            }
        }

        private static string GetProposedText(TextBox tb, string newText)
        {
            string currentText = tb.Text ?? string.Empty;
            int selectionStart = tb.SelectionStart;
            int selectionLength = tb.SelectionLength;

            if (selectionLength > 0 && selectionStart + selectionLength <= currentText.Length)
            {
                currentText = currentText.Remove(selectionStart, selectionLength);
            }

            return currentText.Insert(selectionStart, newText);
        }

        private static bool IsQuantityTextValid(string text, bool isFractional)
        {
            if (string.IsNullOrEmpty(text)) return true;

            if (!isFractional)
            {
                // Non-fractional products: strictly numeric '0'-'9'
                foreach (char c in text)
                {
                    if (c < '0' || c > '9') return false;
                }
                return true;
            }
            else
            {
                // Fractional products: '0'-'9' and single decimal separator (. or ,)
                int separatorCount = 0;
                int separatorIndex = -1;

                for (int i = 0; i < text.Length; i++)
                {
                    char c = text[i];
                    if (c == '.' || c == ',')
                    {
                        separatorCount++;
                        separatorIndex = i;
                        if (separatorCount > 1) return false;
                    }
                    else if (c < '0' || c > '9')
                    {
                        return false;
                    }
                }

                // Max 3 decimal digits
                if (separatorIndex >= 0)
                {
                    int decimalDigits = text.Length - 1 - separatorIndex;
                    if (decimalDigits > 3) return false;
                }

                return true;
            }
        }
    }
}
