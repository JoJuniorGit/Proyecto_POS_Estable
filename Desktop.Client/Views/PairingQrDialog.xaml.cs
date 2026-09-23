using System;
using System.ComponentModel;
using System.Threading.Tasks;
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
        _viewModel.RequestClipboardCopy += OnRequestClipboardCopy;

        Loaded += (s, e) =>
        {
            _ = InitializeAndRenderAsync();
        };

        Closed += (s, e) =>
        {
            _viewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _viewModel.RequestClipboardCopy -= OnRequestClipboardCopy;
            (DataContext as IDisposable)?.Dispose();
        };
    }

    private void OnRequestClipboardCopy(string textToCopy)
    {
        try
        {
            Clipboard.SetText(textToCopy);
        }
        catch
        {
        }
    }

    private async Task InitializeAndRenderAsync()
    {
        try
        {
            await _viewModel.InitializeAsync();
            RenderQrCode();
        }
        catch
        {
        }
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
