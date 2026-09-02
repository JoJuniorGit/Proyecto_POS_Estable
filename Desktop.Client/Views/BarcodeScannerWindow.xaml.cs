using System;
using System.Collections.ObjectModel;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Desktop.Client.Services;
using MaterialDesignThemes.Wpf;
using ZXing;
using ZXing.Windows.Compatibility;

namespace Desktop.Client.Views;

/// <summary>
/// Floating utility window: live webcam feed with real-time barcode decoding (ZXing.Net)
/// and an on-demand text (OCR) mode using the native Windows OCR engine.
/// All frames stay in memory; nothing is ever written to disk.
/// </summary>
public partial class BarcodeScannerWindow : Window
{
    private const int MaxHistoryItems = 5;
    private const int FrameIntervalMs = 40; // governs the preview/scan cadence

    // Enfriamiento SOLO para el mismo código re-presentado de inmediato (1.8 s): evita
    // dobles disparos accidentales del mismo producto mientras sigue frente a la cámara.
    // Un producto DISTINTO se escanea al instante, sin ninguna espera.
    private const double SameCodeCooldownSeconds = 1.8;

    private static readonly Brush CameraIdleBrush = CreateBrush("#9E9E9E");
    private static readonly Brush CameraActiveBrush = CreateBrush("#2E7D32");
    private static readonly Brush CameraErrorBrush = CreateBrush("#C62828");

    // Result card state accents (Material Design friendly tones)
    private static readonly Brush ResultFoundBrush = CreateBrush("#2E7D32");   // green — product found
    private static readonly Brush ResultWarnBrush = CreateBrush("#E65100");    // deep orange — not found / not addable
    private static readonly Brush ResultErrorBrush = CreateBrush("#C62828");   // red — inactive / error
    private static readonly Brush ResultNeutralBrush = CreateBrush("#546E7A"); // blue-gray — no resolver available
    private static readonly Brush ResultTitleDefaultBrush = CreateBrush("#ECEFF1");

    private readonly BarcodeScannerService _scannerService = new();
    private readonly OcrService? _ocrService;
    private readonly IScannerFeedbackService _feedbackService;
    private readonly Action<string>? _onValueReady;
    private readonly Func<string, Task<Core.DTOs.ProductQuickInfoDto?>>? _productResolver;
    private readonly BarcodeReaderBitmapSource _barcodeReader = new()
    {
        AutoRotate = true,
        Options =
        {
            TryHarder = false,
            PossibleFormats = new System.Collections.Generic.List<BarcodeFormat>
            {
                BarcodeFormat.EAN_13,
                BarcodeFormat.EAN_8,
                BarcodeFormat.UPC_A,
                BarcodeFormat.UPC_E,
                BarcodeFormat.CODE_128,
                BarcodeFormat.CODE_39,
                BarcodeFormat.ITF,
                BarcodeFormat.CODABAR
            }
        }
    };

    private CancellationTokenSource? _captureCts;
    private bool _isProcessing;
    private bool _isClosed;
    private bool _cameraErrorShown;
    private string? _lastCode;
    private DateTime _lastCopyAt = DateTime.MinValue;
    private int _resultSeq;

    public ObservableCollection<string> History { get; } = new();

    /// <param name="onValueReady">Invoked (on the UI thread) whenever a value is copied:
    /// a scanned code, recognized OCR text, or a history item. The POS uses it to add the
    /// product to the cart (barcodes) or fill its search box.</param>
    /// <param name="productResolver">Resolves a scanned code to its product (exact SKU).
    /// When provided, the window shows the product name / "not found" / "inactive" states
    /// on the result card; when null, it falls back to a neutral "code copied" card.</param>
    /// <param name="feedbackService">Audio/haptic feedback service for differentiated cues.</param>
    public BarcodeScannerWindow(
        Action<string>? onValueReady = null,
        Func<string, Task<Core.DTOs.ProductQuickInfoDto?>>? productResolver = null,
        IScannerFeedbackService? feedbackService = null)
    {
        InitializeComponent();
        _onValueReady = onValueReady;
        _productResolver = productResolver;
        _feedbackService = feedbackService ?? new ScannerFeedbackService();
        _ocrService = OcrService.TryCreate();
        HistoryList.ItemsSource = History;

        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetCameraState("Starting camera…", CameraIdleBrush);
        SetStatus("Starting camera…");

        var init = await _scannerService.InitializeAsync();
        if (_isClosed)
        {
            _scannerService.Dispose();
            return;
        }

        if (!init.Success)
        {
            SetCameraState("Camera unavailable", CameraErrorBrush);
            ShowPlaceholder(init.Message);
            SetStatus("Camera unavailable.", isError: true);
            return;
        }

        SetCameraState("Camera active", CameraActiveBrush);
        HidePlaceholder();

        if (_ocrService == null)
        {
            CaptureTextButton.IsEnabled = false;
            OcrLanguageText.Text = "OCR language pack not installed on this Windows.";
        }
        else
        {
            OcrLanguageText.Text = $"OCR language: {_ocrService.LanguageTag}";
        }

        // The overlay status strip is what the cashier reads; make it leave the
        // "Starting camera…" state the moment the camera is actually live.
        SetStatus("Point the camera at a barcode…");

        StartCaptureLoop();
    }

    private void SetCameraState(string status, Brush dotBrush)
    {
        CameraDot.Fill = dotBrush;
        CameraStatusText.Text = status;
    }

    private void ShowPlaceholder(string message)
    {
        PreviewPlaceholder.Visibility = Visibility.Visible;
        PreviewPlaceholderText.Text = message;
        PreviewImage.Source = null;
        ScanOverlay.Visibility = Visibility.Collapsed;
    }

    private void HidePlaceholder()
    {
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        ScanOverlay.Visibility = BarcodeModeRadio.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void StartCaptureLoop()
    {
        _captureCts?.Cancel();
        _captureCts = new CancellationTokenSource();
        var token = _captureCts.Token;

        try
        {
            while (!token.IsCancellationRequested && !_isClosed)
            {
                // While an OCR capture is in flight, don't fight over the camera.
                if (_isProcessing)
                {
                    try { await Task.Delay(60, token); } catch (OperationCanceledException) { return; }
                    continue;
                }

                ScannerFrame? frame;
                try
                {
                    frame = await _scannerService.CaptureFrameAsync();
                }
                catch
                {
                    frame = null;
                }

                if (_isClosed) return;

                if (frame == null)
                {
                    if (!_cameraErrorShown)
                    {
                        _cameraErrorShown = true;
                        SetStatus("Camera error — retrying…", isError: true);
                    }
                    try { await Task.Delay(1000, token); } catch (OperationCanceledException) { return; }
                    continue;
                }

                PreviewImage.Source = frame.Bitmap;

                // Recover the status strip once frames flow again after an error.
                if (_cameraErrorShown)
                {
                    _cameraErrorShown = false;
                    SetStatus("Point the camera at a barcode…");
                }

                if (BarcodeModeRadio.IsChecked == true)
                {
                    TryDecode(frame.Bitmap);
                }

                try { await Task.Delay(FrameIntervalMs, token); } catch (OperationCanceledException) { return; }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            SetStatus("Camera stopped unexpectedly.", isError: true);
        }
    }

    private void TryDecode(BitmapSource bitmap)
    {
        if (_isProcessing) return;
        _isProcessing = true;
        try
        {
            var roi = BarcodeScannerService.ExtractRoi(bitmap);
            var bgra = BarcodeScannerService.ToBgra32(roi);
            var result = _barcodeReader.Decode(bgra);

            if (result != null && !string.IsNullOrWhiteSpace(result.Text))
            {
                var code = result.Text.Trim();

                // Restricción estricta: Ignorar QR, URLs y texto arbitrario
                if (!Core.Helpers.BarcodeValidator.IsValidBarcode(code))
                {
                    return;
                }

                var now = DateTime.Now;

                // Enfriamiento SOLO para el mismo código: si el mismo producto se vuelve
                // a presentar dentro de la ventana de 1.8 s se ignora (evita dobles
                // disparos accidentales). Un código DISTINTO se procesa de inmediato.
                bool sameCodeWithinCooldown = code == _lastCode
                    && (now - _lastCopyAt).TotalSeconds < SameCodeCooldownSeconds;

                // El último código visto se recuerda siempre (aunque se suprima), para
                // que al presentarlo de nuevo después del enfriamiento vuelva a disparar.
                _lastCode = code;

                if (!sameCodeWithinCooldown)
                {
                    _lastCopyAt = now;
                    CooldownProgress.Visibility = Visibility.Collapsed;
                    HandleValueReady(code, $"Code copied: {code}  ({result.BarcodeFormat})");
                    _ = ShowProductResultAsync(code, result.BarcodeFormat.ToString());
                }
                else
                {
                    // Feedback visual del cooldown activo
                    CooldownProgress.Visibility = Visibility.Visible;
                    CooldownProgress.BeginAnimation(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
                        new DoubleAnimation(100.0, 0.0, TimeSpan.FromSeconds(SameCodeCooldownSeconds)));
                }
            }
        }
        catch
        {
            // A single bad frame must never take down the POS.
        }
        finally
        {
            _isProcessing = false;
        }
    }

    /// <summary>
    /// Resolves the scanned code against the catalog (exact SKU) and renders the result
    /// card: product name in green, "Producto no encontrado" in amber, inactive / errors
    /// in red. Stale results (a newer scan fired meanwhile) are discarded.
    /// </summary>
    private async Task ShowProductResultAsync(string code, string format)
    {
        var seq = ++_resultSeq;
        try
        {
            Core.DTOs.ProductQuickInfoDto? info = null;
            if (_productResolver != null)
            {
                info = await _productResolver(code);
            }

            if (seq != _resultSeq || _isClosed) return;

            if (_productResolver == null)
            {
                // Standalone mode (no POS context): neutral card with the raw code.
                _feedbackService.PlaySuccess();
                ShowResultCard(PackIconKind.Barcode, ResultNeutralBrush,
                    title: code, titleBrush: null,
                    subtitle: $"Format: {format}");
                return;
            }

            if (info == null)
            {
                _feedbackService.PlayNotFound();
                ShowResultCard(PackIconKind.AlertCircle, ResultWarnBrush,
                    title: "Producto no encontrado", titleBrush: ResultWarnBrush,
                    subtitle: code);
                return;
            }

            if (!info.IsActive)
            {
                _feedbackService.PlayError();
                ShowResultCard(PackIconKind.CloseCircle, ResultErrorBrush,
                    title: "Producto inactivo", titleBrush: ResultErrorBrush,
                    subtitle: $"{code}  •  {info.Name}");
                return;
            }

            if (info.IsCashAdvance)
            {
                _feedbackService.PlayNotFound();
                ShowResultCard(PackIconKind.AlertCircle, ResultWarnBrush,
                    title: info.Name, titleBrush: ResultWarnBrush,
                    subtitle: $"{code}  •  Sistema — requiere captura manual");
                return;
            }

            // Found and active: show the product name and price in green.
            _feedbackService.PlaySuccess();
            string price = info.PriceBsS > 0 ? $"Bs.S {info.PriceBsS:N2}" : $"USD {info.PriceUSD:N2}";
            ShowResultCard(PackIconKind.CheckCircle, ResultFoundBrush,
                title: info.Name, titleBrush: null,
                subtitle: $"{code}  •  {format}  •  {price}");
        }
        catch (Exception)
        {
            if (seq != _resultSeq || _isClosed) return;
            // Error de lectura: no se pudo resolver el código; se muestra el código
            // escaneado como referencia, igual que en el estado "Producto no encontrado".
            _feedbackService.PlayError();
            ShowResultCard(PackIconKind.CloseCircle, ResultErrorBrush,
                title: "No se pudo leer el código", titleBrush: ResultErrorBrush,
                subtitle: code);
        }
    }

    private void ShowResultCard(PackIconKind iconKind, Brush accent, string title, Brush? titleBrush, string subtitle)
    {
        ResultIcon.Kind = iconKind;
        ResultIcon.Foreground = accent;
        ResultTitle.Text = title;
        ResultTitle.Foreground = titleBrush ?? ResultTitleDefaultBrush;
        ResultSubtitle.Text = subtitle;
        ResultCard.BorderBrush = accent;

        // Quick, non-blocking fade-in.
        ResultCard.Visibility = Visibility.Visible;
        ResultCard.BeginAnimation(UIElement.OpacityProperty, null);
        ResultCard.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(160)));
    }

    private void Mode_Changed(object sender, RoutedEventArgs e)
    {
        // The Checked event can fire while the XAML is still being loaded; skip then.
        if (OcrPanel == null || ScanOverlay == null || StatusText == null) return;

        bool ocrMode = OcrModeRadio.IsChecked == true;
        OcrPanel.Visibility = ocrMode ? Visibility.Visible : Visibility.Collapsed;
        ScanOverlay.Visibility = !ocrMode && PreviewImage.Source != null ? Visibility.Visible : Visibility.Collapsed;

        if (ocrMode && _ocrService == null)
        {
            SetStatus("Text (OCR) is unavailable: no Windows OCR language pack is installed.", isError: true);
        }
        else if (ocrMode)
        {
            SetStatus("Point the camera at printed text, then press \"Capture & Read Text\".");
        }
        else
        {
            SetStatus("Point the camera at a barcode…");
        }
    }

    private async void CaptureText_Click(object sender, RoutedEventArgs e)
    {
        if (_ocrService == null || _isProcessing) return;
        _isProcessing = true;
        try
        {
            SetStatus("Reading text…");
            var frame = await _scannerService.CaptureFrameAsync();
            if (frame == null)
            {
                SetStatus("Could not capture a frame from the camera.", isError: true);
                return;
            }

            var text = await _ocrService.RecognizeAsync(frame.JpegBytes);
            OcrTextBox.Text = text ?? string.Empty;
            CopyTextButton.IsEnabled = !string.IsNullOrWhiteSpace(OcrTextBox.Text);

            if (string.IsNullOrWhiteSpace(OcrTextBox.Text))
            {
                SetStatus("No text recognized. Try again with better lighting or a closer shot.", isError: true);
            }
            else
            {
                OcrTextBox.SelectAll();
                OcrTextBox.Focus();
                SetStatus("Text recognized. Select a portion or press \"Copy text\".");
            }
        }
        catch (Exception ex)
        {
            SetStatus("OCR failed: " + ex.Message, isError: true);
        }
        finally
        {
            _isProcessing = false;
        }
    }

    private void CopyOcrText_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(OcrTextBox.Text)) return;
        var text = OcrTextBox.Text.Trim();
        HandleValueReady(text, "Text copied: " + Truncate(text, 42));
        SystemSounds.Asterisk.Play();
    }

    private void HistoryList_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (HistoryList.SelectedItem is string value)
        {
            HandleValueReady(value, "Copied: " + Truncate(value, 42));
            SystemSounds.Asterisk.Play();
            HistoryList.SelectedItem = null;
        }
    }

    private void HandleValueReady(string value, string status)
    {
        try { Clipboard.SetText(value); } catch { /* clipboard may be locked by another app */ }

        History.Remove(value);
        History.Insert(0, value);
        while (History.Count > MaxHistoryItems) History.RemoveAt(History.Count - 1);

        SetStatus(status);
        _onValueReady?.Invoke(value);
    }

    private void SetStatus(string text, bool isError = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = isError ? Brushes.IndianRed : Brushes.White;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosed(object? sender, EventArgs e)
    {
        _isClosed = true;
        try
        {
            _captureCts?.Cancel();
            _captureCts?.Dispose();
            _scannerService.Dispose();
            if (_feedbackService is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        catch { }
    }

    private static string Truncate(string text, int maxLength)
        => text.Length <= maxLength ? text : text.Substring(0, maxLength) + "…";

    private static Brush CreateBrush(string hex)
    {
        var brush = (Brush)new BrushConverter().ConvertFromString(hex)!;
        brush.Freeze();
        return brush;
    }
}
