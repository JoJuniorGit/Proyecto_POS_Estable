namespace Core.DTOs;

/// <summary>
/// 8.184-T1 (S1): políticas globales de precios y redondeo del sistema. Los tres valores se
/// persisten como settings independientes (enum por NOMBRE y bool) y este DTO viaja como respuesta
/// de GET/PUT y como cuerpo del PUT (reemplazo completo, con defaults seguros).
/// El lector resuelve ExactMath / ExactMath / true cuando la clave falta o es ilegible.
/// </summary>
public class PricingPoliciesDto
{
    /// <summary>Regla de redondeo USD por defecto para detalle y al mayor (UI: Sin redondeo / Psicológico).</summary>
    public RoundingRuleType UsdRoundingRule { get; set; } = RoundingRuleType.ExactMath;

    /// <summary>Regla de efectivo Bs.S por defecto (UI: Sin redondeo / Entero / ×5 / ×10); solo proyecta en caliente.</summary>
    public RoundingRuleType CashBsRoundingRule { get; set; } = RoundingRuleType.ExactMath;

    /// <summary>Protección de piso de margen ACTIVADA por defecto (decisión S1 del mantenedor).</summary>
    public bool EnforceMinimumMarginFloor { get; set; } = true;
}
