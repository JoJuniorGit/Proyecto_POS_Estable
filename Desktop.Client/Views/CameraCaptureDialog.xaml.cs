using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Desktop.Client.Services;
using OpenCvSharp.WpfExtensions;

namespace Desktop.Client.Views;

/// <summary>
/// 8.147-S4 (T7): modal de captura por cámara con preview en vivo (~25 FPS). Sin cámara muestra
/// un mensaje claro y no rompe la aplicación (L4: el camino de archivo sigue disponible).
/// </summary>
public partial class CameraCaptureDialog : Window
{
    private static readonly TimeSpan PreviewInterval = TimeSpan.FromMilliseconds(40);

    private readonly ICameraCaptureService _cameraService;
    private readonly DispatcherTimer _previewTimer;

    /// <summary>Ruta del JPG temporal capturado; null si el modal se canceló o no hubo cámara.</summary>
    public string? CapturedFilePath { get; private set; }

    public CameraCaptureDialog(ICameraCaptureService cameraService)
    {
        ArgumentNullException.ThrowIfNull(cameraService);
        _cameraService = cameraService;
        InitializeComponent();

        _previewTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = PreviewInterval
        };
        _previewTimer.Tick += OnPreviewTick;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_cameraService.TryOpen())
        {
            ShowCameraUnavailableMessage();
            return;
        }

        _previewTimer.Start();
    }

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        if (!_cameraService.TryReadFrame(out var frame))
        {
            ShowCameraUnavailableMessage();
            return;
        }

        using (frame)
        {
            try
            {
                var bitmap = frame.ToBitmapSource();
                bitmap.Freeze();
                PreviewImage.Source = bitmap;
                CaptureErrorMessage.Visibility = Visibility.Collapsed;
            }
            catch (Exception)
            {
                // Un frame inválido no debe cerrar el diálogo; se reintenta en el siguiente tick.
                CaptureErrorMessage.Text = "No se pudo mostrar el preview de la cámara.";
                CaptureErrorMessage.Visibility = Visibility.Visible;
            }
        }
    }

    private void CaptureButton_Click(object sender, RoutedEventArgs e)
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"ocr-camara-{Guid.NewGuid():N}.jpg");
        if (!_cameraService.TrySaveFrame(filePath))
        {
            CaptureErrorMessage.Text = "No se pudo capturar la foto. Verifique la cámara e intente de nuevo.";
            CaptureErrorMessage.Visibility = Visibility.Visible;
            return;
        }

        CapturedFilePath = filePath;
        DialogResult = true;
    }

    private void ShowCameraUnavailableMessage()
    {
        _previewTimer.Stop();
        PreviewImage.Visibility = Visibility.Collapsed;
        CameraUnavailableMessage.Visibility = Visibility.Visible;
        CaptureButton.IsEnabled = false;
        CaptureErrorMessage.Visibility = Visibility.Collapsed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _previewTimer.Stop();
        _previewTimer.Tick -= OnPreviewTick;
        Loaded -= OnLoaded;
        Closed -= OnClosed;
    }
}
