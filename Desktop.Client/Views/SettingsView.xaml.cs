using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views
{
    public partial class SettingsView : UserControl
    {
        public SettingsView()
        {
            InitializeComponent();
        }

        private async void CopyServerAddress_Click(object sender, RoutedEventArgs e)
        {
            if (DataContext is not SettingsViewModel viewModel) return;

            try
            {
                Clipboard.SetText(viewModel.CurrentServerAddress);
                if (CopyServerFeedbackText is null) return;

                CopyServerFeedbackText.Visibility = Visibility.Visible;
                await Task.Delay(1600);
                CopyServerFeedbackText.Visibility = Visibility.Collapsed;
            }
            catch
            {
                // Clipboard ocupado o no disponible: no interrumpe la vista de configuración.
            }
        }
    }
}