using System;
using OpenCvSharp;

namespace Desktop.Client.Services;

/// <summary>
/// 8.147-D5/S4 (T7): acceso a la cámara por defecto del dispositivo. Todas las operaciones
/// devuelven false en vez de lanzar cuando no hay cámara o falla la captura, para que la
/// ausencia de cámara degrade a la carga por archivo sin romper la aplicación.
/// </summary>
public interface ICameraCaptureService : IDisposable
{
    /// <summary>true cuando la cámara por defecto quedó abierta y lista para leer frames.</summary>
    bool TryOpen();

    /// <summary>Lee el frame actual. El llamador es responsable de disponer el <see cref="Mat"/>.</summary>
    bool TryReadFrame(out Mat frame);

    /// <summary>Guarda el frame actual como JPG en <paramref name="filePath"/>; false si falla.</summary>
    bool TrySaveFrame(string filePath);
}
