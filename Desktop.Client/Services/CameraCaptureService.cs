using System;
using System.Threading;
using OpenCvSharp;

namespace Desktop.Client.Services;

/// <summary>
/// 8.147-D5/S4 (T7): implementación con OpenCvSharp <see cref="VideoCapture"/> sobre la cámara
/// por defecto (índice 0). Sin runtime nativo o sin cámara, cada método devuelve false.
/// </summary>
public sealed class CameraCaptureService : ICameraCaptureService
{
    private readonly object _sync = new();
    private VideoCapture? _capture;
    private int _disposed;

    public bool TryOpen()
    {
        lock (_sync)
        {
            if (_disposed != 0)
            {
                return false;
            }

            if (_capture is not null)
            {
                return _capture.IsOpened();
            }

            try
            {
                var capture = new VideoCapture(0);
                if (!capture.IsOpened())
                {
                    capture.Dispose();
                    return false;
                }

                _capture = capture;
                return true;
            }
            catch (Exception)
            {
                // Sin cámara (o sin nativos de OpenCV) la captura no debe romper el flujo de archivo.
                return false;
            }
        }
    }

    public bool TryReadFrame(out Mat frame)
    {
        frame = new Mat();
        lock (_sync)
        {
            if (_disposed != 0 || _capture is null || !_capture.IsOpened())
            {
                return false;
            }

            try
            {
                if (!_capture.Read(frame) || frame.Empty())
                {
                    frame.Dispose();
                    frame = new Mat();
                    return false;
                }

                return true;
            }
            catch (Exception)
            {
                frame.Dispose();
                frame = new Mat();
                return false;
            }
        }
    }

    public bool TrySaveFrame(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return false;
        }

        try
        {
            if (!TryReadFrame(out var frame))
            {
                return false;
            }

            using (frame)
            {
                return Cv2.ImWrite(filePath, frame);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        lock (_sync)
        {
            _capture?.Release();
            _capture?.Dispose();
            _capture = null;
        }
    }
}
