using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Desktop.Client.Helpers;

namespace Desktop.Client.Views
{
    public partial class PosView : UserControl
    {
        private KeyboardWedgeScannerListener? _scannerListener;

        public PosView()
        {
            InitializeComponent();
            Loaded += PosView_Loaded;
            Unloaded += PosView_Unloaded;
        }

        private void PosView_Loaded(object sender, RoutedEventArgs e)
        {
            _scannerListener?.Dispose();
            _scannerListener = new KeyboardWedgeScannerListener(async code =>
            {
                if (DataContext is ViewModels.PosViewModel vm)
                {
                    await vm.AddProductByCodeAsync(code);
                }
            });
            _scannerListener.Attach(this);
        }

        private void PosView_Unloaded(object sender, RoutedEventArgs e)
        {
            _scannerListener?.Dispose();
            _scannerListener = null;
        }

        private void UserControl_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F2)
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
            Dispatcher.BeginInvoke(new Action(() =>
            {
                SearchInput.Focus();
                SearchInput.SelectAll();
            }), System.Windows.Threading.DispatcherPriority.Input);
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

        private void DataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
        {
            if (e.Row.DataContext is ViewModels.CartItemViewModel itemVm && DataContext is ViewModels.PosViewModel posVm)
            {
                if (e.EditingElement is TextBox tb)
                {
                    string text = tb.Text;
                    if (decimal.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out decimal q) ||
                        decimal.TryParse(text, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.CurrentCulture, out q))
                    {
                        decimal rounded = System.Math.Round(q, 3, System.MidpointRounding.AwayFromZero);
                        _ = posVm.Cart.CommitItemQuantityAsync(itemVm.Id, rounded);
                    }
                }
            }
        }
    }
}
