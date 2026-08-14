using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Devices.Enumeration;
using Windows.Media.Capture;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;

namespace Desktop.Client.Services;

/// <summary>
/// Result of attempting to initialize the camera.
/// </summary>
public sealed class CameraInitResult
{
    public bool Success { get; private init; }
    public string Message { get; private init; } = string.Empty;

    public static CameraInitResult Ok() => new() { Success = true };
    public static CameraInitResult Fail(string message) => new() { Success = false, Message = message };
}

/// <summary>
/// A single camera frame kept only in memory: the raw JPEG bytes (for OCR) and a
/// WPF BitmapSource (for preview and barcode decoding). Nothing is written to disk.
/// </summary>
public sealed class ScannerFrame
{
    public byte[] JpegBytes { get; }
    public BitmapSource Bitmap { get; }

    public ScannerFrame(byte[] jpegBytes, BitmapSource bitmap)
    {
        JpegBytes = jpegBytes;
        Bitmap = bitmap;
    }
}

/// <summary>
/// Manages the webcam via Windows.Media.Capture (WinRT). Frames are captured on demand
/// as in-memory JPEG snapshots, which keeps the WPF UI free of UWP XAML dependencies and
/// guarantees that no image data is ever persisted.
/// </summary>
public sealed class BarcodeScannerService : IDisposable
{
    private readonly SemaphoreSlim _captureLock = new(1, 1);

    // Smaller photo size = faster JPEG encode = higher preview/scan FPS. Most webcams
    // honor these; if not, CaptureFrameAsync falls back to the camera's native size.
    private const uint CaptureWidth = 960;
    private const uint CaptureHeight = 540;

    // The preview/decode bitmap is capped so JPEG decoding and ZXing stay cheap even when
    // the driver ignores the requested size and returns a full 1080p/4K frame. OCR still
    // receives the raw, full-resolution JPEG bytes via ScannerFrame.JpegBytes.
    private const int DecodePixelWidth = 800;

    private MediaCapture? _capture;
    private bool _disposed;

    /// <summary>
    /// Finds the default webcam and initializes MediaCapture.
    /// </summary>
    public async Task<CameraInitResult> InitializeAsync()
    {
        if (_capture != null) return CameraInitResult.Ok();

        try
        {
            var devices = await DeviceInformation.FindAllAsync(DeviceClass.VideoCapture);
            var camera = devices.FirstOrDefault(d => d.IsEnabled) ?? devices.FirstOrDefault();

            if (camera == null)
            {
                return CameraInitResult.Fail(
                    "No camera was found on this device. Connect a webcam and try again.");
            }

            var capture = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video,
                VideoDeviceId = camera.Id,
                MediaCategory = MediaCategory.Media
            };

            await capture.InitializeAsync(settings);
            _capture = capture;
            return CameraInitResult.Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return CameraInitResult.Fail(
                "Camera access was denied. Enable \"Let desktop apps access your camera\" in " +
                "Windows Settings → Privacy & security → Camera, then reopen this tool.");
        }
        catch (Exception ex)
        {
            return CameraInitResult.Fail(
                "Could not start the camera: " + ex.Message);
        }
    }

    /// <summary>
    /// Captures a single frame. Returns null when the camera is unavailable or a capture fails.
    /// Captures are serialized so the scan loop and an on-demand OCR capture never collide.
    /// </summary>
    public async Task<ScannerFrame?> CaptureFrameAsync()
    {
        if (_capture == null || _disposed) return null;

        var bytes = await CaptureJpegAsync(CreateSizedJpegProperties());
        bytes ??= await CaptureJpegAsync(null); // fall back to native camera resolution

        if (bytes == null || bytes.Length == 0) return null;

        var bitmap = CreateBitmapSource(bytes);
        if (bitmap == null) return null;

        return new ScannerFrame(bytes, bitmap);
    }

    private static ImageEncodingProperties CreateSizedJpegProperties()
    {
        var props = ImageEncodingProperties.CreateJpeg();
        props.Width = CaptureWidth;
        props.Height = CaptureHeight;
        return props;
    }

    private async Task<byte[]?> CaptureJpegAsync(ImageEncodingProperties? props)
    {
        if (_capture == null || _disposed) return null;

        await _captureLock.WaitAsync();
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await _capture.CapturePhotoToStreamAsync(props ?? ImageEncodingProperties.CreateJpeg(), stream);
            stream.Seek(0);

            var reader = new DataReader(stream);
            using (reader)
            {
                await reader.LoadAsync((uint)stream.Size);
                var bytes = new byte[stream.Size];
                reader.ReadBytes(bytes);
                return bytes;
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            _captureLock.Release();
        }
    }

    private static BitmapSource? CreateBitmapSource(byte[] jpegBytes)
    {
        try
        {
            var bitmap = new BitmapImage();
            using (var ms = new System.IO.MemoryStream(jpegBytes))
            {
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = DecodePixelWidth;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
            }
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Converts the frame to Bgra32 so ZXing's BitmapSource luminance source handles it safely.
    /// </summary>
    public static BitmapSource ToBgra32(BitmapSource source)
    {
        if (source.Format == System.Windows.Media.PixelFormats.Bgra32) return source;

        var converted = new FormatConvertedBitmap();
        converted.BeginInit();
        converted.Source = source;
        converted.DestinationFormat = System.Windows.Media.PixelFormats.Bgra32;
        converted.EndInit();
        converted.Freeze();
        return converted;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _capture?.Dispose(); } catch { }
        _capture = null;
        _captureLock.Dispose();
    }
}
