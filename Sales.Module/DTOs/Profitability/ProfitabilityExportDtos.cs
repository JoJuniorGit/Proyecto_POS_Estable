namespace Sales.Module.DTOs.Profitability;

/// <summary>
/// Paso 14 (F3-S2): metadatos de emisión de la exportación del Estado de Resultados. El
/// controlador resuelve la empresa y el usuario; el servicio solo los estampa en el archivo.
/// </summary>
public sealed record ProfitabilityExportContext
{
    public string CompanyName { get; init; } = "CommandCenter POS";

    public string ExportedBy { get; init; } = "Usuario";

    public string FilterSummary { get; init; } = string.Empty;

    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Paso 14 (F3-S2): archivo exportado listo para devolverse como <c>File(...)</c> sin volver a
/// serializar el contenido.
/// </summary>
public sealed record ProfitabilityExportFile
{
    public byte[] Content { get; init; } = [];

    public string ContentType { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;
}
