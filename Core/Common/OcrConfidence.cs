namespace Core.Common;

/// <summary>
/// 8.147-D9: bandas de confianza OCR compartidas por servidor y cliente. El cliente resalta
/// en amarillo por debajo de <see cref="YellowBelow"/> y en rojo por debajo de
/// <see cref="RedBelow"/>; un campo no resuelto lleva <see cref="Unresolved"/> (0).
/// </summary>
public static class OcrConfidence
{
    /// <summary>Confianza inferior a este valor (porcentaje 0–100) se considera roja.</summary>
    public const decimal RedBelow = 60m;

    /// <summary>Confianza inferior a este valor (y no roja) se considera amarilla.</summary>
    public const decimal YellowBelow = 85m;

    /// <summary>Confianza de un campo que el OCR no pudo resolver (S3: no resuelto = 0).</summary>
    public const decimal Unresolved = 0m;
}
