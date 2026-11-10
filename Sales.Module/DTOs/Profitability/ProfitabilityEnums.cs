namespace Sales.Module.DTOs.Profitability;

/// <summary>
/// Paso 14 (F2-S1): preset del período consultado. Se interpreta en la Zona Horaria Global y se
/// convierte a límites UTC estrictos; no depende de la zona horaria del servidor.
/// </summary>
public enum ProfitabilityPeriodPreset
{
    Today = 0,
    Yesterday = 1,
    Week = 2,
    Month = 3,
    Custom = 4
}

/// <summary>
/// Paso 14 (F2-S4): granularidad de la serie temporal del Estado de Resultados.
/// </summary>
public enum ProfitabilityGroupBy
{
    Hour = 0,
    Day = 1,
    Week = 2,
    Month = 3
}

/// <summary>
/// Paso 14 (F2-S4): severidad de las alertas de erosión de margen.
/// </summary>
public enum ProfitabilityAlertSeverity
{
    Info = 0,
    Warning = 1,
    Critical = 2
}
