using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Desktop.Client.Controls
{
    public partial class OmniboxControl : UserControl
    {
        // Dependency Properties or Events could be added here to communicate with ViewModel
        // For simplicity, we assume DataContext is the ViewModel

        public static readonly DependencyProperty SearchCommandProperty =
            DependencyProperty.Register("SearchCommand", typeof(ICommand), typeof(OmniboxControl), new PropertyMetadata(null));

        public ICommand SearchCommand
        {
            get { return (ICommand)GetValue(SearchCommandProperty); }
            set { SetValue(SearchCommandProperty, value); }
        }

        public static readonly DependencyProperty ScanCommandProperty =
            DependencyProperty.Register("ScanCommand", typeof(ICommand), typeof(OmniboxControl), new PropertyMetadata(null));

        public ICommand ScanCommand
        {
            get { return (ICommand)GetValue(ScanCommandProperty); }
            set { SetValue(ScanCommandProperty, value); }
        }

        public static readonly DependencyProperty PlaceholderProperty =
            DependencyProperty.Register("Placeholder", typeof(string), typeof(OmniboxControl), new PropertyMetadata(string.Empty));

        public string Placeholder
        {
            get { return (string)GetValue(PlaceholderProperty); }
            set { SetValue(PlaceholderProperty, value); }
        }

        public void FocusInput()
        {
            InputBox.Focus();
        }

        public OmniboxControl()
        {
            InitializeComponent();
        }

        private void InputBox_GotFocus(object sender, RoutedEventArgs e)
        {
            // Empty placeholder for GotFocus event, as we removed the popup
        }

        private void InputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Empty placeholder, we no longer want search-as-you-type
        }

        private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;

                var text = InputBox.Text;
                if (string.IsNullOrWhiteSpace(text)) return;

                // Priority to ScanCommand if available and applicable, otherwise Default to SearchCommand
                if (ScanCommand?.CanExecute(text) == true)
                {
                    ScanCommand.Execute(text);
                }
                else if (SearchCommand?.CanExecute(text) == true)
                {
                    SearchCommand.Execute(text);
                }

                // Decide whether to clear. For a search box, usually we leave the text or select it.
                // Keeping clear for scanner consistency if that was the original intent.
                InputBox.Clear();
            }
        }
    }
}
