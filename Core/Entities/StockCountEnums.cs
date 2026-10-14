namespace Core.Entities;

/// <summary>
/// 8.180-T1 (S1/S2): ciclo de vida de una sesión de conteo físico ciego (Paso 6 del roadmap).
/// Open → se escanea/corrige; Closed → teórico y costo congelados por ítem; Applied → ajustes de
/// stock aplicados (terminal); Cancelled → descartada (terminal). Se persiste como string(32).
/// </summary>
public enum StockCountStatus
{
    Open,
    Closed,
    Applied,
    Cancelled
}
