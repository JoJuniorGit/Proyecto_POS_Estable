namespace Desktop.Client.Services;

/// <summary>
/// Proporciona retroalimentación auditiva diferenciada y no bloqueante para el escáner POS.
/// </summary>
public interface IScannerFeedbackService
{
    /// <summary>
    /// Emite un tono agudo y limpio (880 Hz) indicando que el producto fue reconocido y agregado a la venta.
    /// </summary>
    void PlaySuccess();

    /// <summary>
    /// Emite un tono medio de advertencia (440 Hz) indicando que el código no está registrado en el inventario.
    /// </summary>
    void PlayNotFound();

    /// <summary>
    /// Emite un tono grave (220 Hz) indicando que el producto está inactivo o se produjo un error.
    /// </summary>
    void PlayError();
}
