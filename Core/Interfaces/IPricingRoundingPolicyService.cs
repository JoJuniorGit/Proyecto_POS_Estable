using Core.DTOs;

namespace Core.Interfaces;

/// <summary>
/// 8.182-T1 (S1): política pura de redondeo comercial/psicológico con guarda de piso de margen.
/// Sin estado ni dependencias de persistencia: apta para DI y para reuso futuro (edición masiva de
/// precios hoy; recálculo por factura y hooks de POS en tramos posteriores).
/// </summary>
public interface IPricingRoundingPolicyService
{
    /// <summary>
    /// Redondea <paramref name="amount"/> según <paramref name="rule"/>. En reglas VES (Bs.S) con
    /// <paramref name="enforceFloor"/> activo aplica techo directo sobre la cota mínima (nunca redondea
    /// hacia abajo; paridad exacta con la proyección de venta 4-args); con el piso inactivo usa el
    /// escalón más cercano. UsdPsychological conserva el piso reactivo: si el escalón elegido con
    /// piso &gt; 0 y costo &gt; 0 deja el margen efectivo por debajo del piso, usa el inmediato
    /// superior. Un importe ≤ 0 pasa sin cambios (el redondeo nunca produce ≤ 0).
    /// </summary>
    RoundingResult Round(decimal amount, RoundingRuleType rule, decimal cost, decimal? marginFloorPct, bool enforceFloor);

    /// <summary>
    /// Escalón inmediato inferior (estrictamente menor) al valor dado para la regla, o null si no
    /// existe uno positivo. Usado por la salvaguarda Detal/Mayor ("bajar Mayor al escalón inmediato
    /// inferior"). UsdPsychological usa la escalera {.25,.50,.75,.95,.99}; ExactMath y las reglas VES
    /// (que operan en Bs.S) devuelven la resolución de centavo en USD.
    /// </summary>
    decimal? FindLowerStep(decimal amount, RoundingRuleType rule);

    /// <summary>
    /// 8.183 (R1): proyecta EN CALIENTE el Bs.S "de efectivo" de un precio USD con la regla VES del
    /// canal y la tasa del día — Round(usd × tasa, regla, costo × tasa, piso, enforceFloor) —. NUNCA
    /// se persiste: el Bs.S almacenado del catálogo es el canónico 8.104 (techo). Devuelve null sin
    /// tasa, sin USD positivo o con una regla distinta de VesCashFriendly*; queda listo para el hook
    /// de POS (activación por settings = residual v1) y la edición masiva lo usa para informar
    /// CashFriendlyAfterBsS.
    /// </summary>
    RoundingResult? ProjectCashFriendlyBsS(decimal usdAmount, decimal exchangeRate, RoundingRuleType rule, decimal costUsd, decimal? marginFloorPct, bool enforceFloor);
}

/// <summary>
/// 8.182-T1 (S1/S2): resultado del redondeo de un importe.
/// MathematicalValue es el importe de entrada (referencia matemática); RoundedValue el valor final
/// aplicado; RoundingDelta = RoundedValue − MathematicalValue (con signo). EffectiveMarginPct es el
/// margen efectivo ((RoundedValue − cost) / costo × 100) del precio FINAL; null si costo ≤ 0.
/// 8.185-perf: readonly record struct — cero asignaciones de heap en la ruta caliente
/// (Catálogo, Modal, POS, Carrito y motor de venta/bulk).
/// </summary>
public readonly record struct RoundingResult(decimal MathematicalValue, decimal RoundedValue, decimal RoundingDelta, decimal? EffectiveMarginPct);
