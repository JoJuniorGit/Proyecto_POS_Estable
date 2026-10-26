namespace Core.DTOs;

/// <summary>
/// 8.211-T1 (S1): régimen monetario de la instalación. <c>Single</c> opera con una sola moneda
/// primaria; <c>Dual</c> exige una moneda secundaria distinta. Los códigos son ISO 4217 alfa-3.
/// </summary>
public enum MonetaryMode
{
    Single = 0,
    Dual = 1
}

/// <summary>
/// 8.211-T1 (S1): perfil monetario configurable. Los defaults son compatibles con la operación
/// histórica USD/Bs.S. <see cref="IsConfigured"/> lo deriva el servicio de la validez del perfil
/// almacenado; un perfil sin configurar conserva los defaults con <c>false</c>.
/// El DTO no aplica atributos de validación: los mensajes de dominio viven en el servicio.
/// </summary>
public sealed class MonetaryProfileDto
{
    public MonetaryMode Mode { get; set; } = MonetaryMode.Dual;
    public string PrimaryCurrencyCode { get; set; } = "USD";
    public string PrimaryCurrencySymbol { get; set; } = "$";
    public string? SecondaryCurrencyCode { get; set; } = "VES";
    public string? SecondaryCurrencySymbol { get; set; } = "Bs.S";
    public bool IsConfigured { get; set; }
}
