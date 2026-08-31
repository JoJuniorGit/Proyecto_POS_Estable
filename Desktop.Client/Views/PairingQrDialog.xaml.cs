using System;
using System.ComponentModel;
using System.Windows;
using Desktop.Client.Helpers;
using Desktop.Client.ViewModels;

namespace Desktop.Client.Views;

public partial class PairingQrDialog : Window
{
    private readonly PairingQrViewModel _viewModel;

    public PairingQrDialog(PairingQrViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = _viewModel;

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.RequestClipboardCopy += textToCopy =>
        {
            try
            {
                Clipboard.SetText(textToCopy);
                MessageBox.Show($"Copiado al portapapeles:\n{textToCopy}", "Copiado", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch { }
        };

        Loaded += async (s, e) =>
        {
            await _viewModel.InitializeAsync();
            RenderQrCode();
        };
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PairingQrViewModel.QrPayload) ||
            e.PropertyName == nameof(PairingQrViewModel.FullUrl))
        {
            RenderQrCode();
        }
    }

    private void RenderQrCode()
    {
        if (!string.IsNullOrWhiteSpace(_viewModel.QrPayload))
        {
            var qrImage = QrCodeHelper.GenerateQrBitmap(_viewModel.QrPayload, 260, 260);
            if (qrImage != null)
            {
                QrImageControl.Source = qrImage;
            }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
