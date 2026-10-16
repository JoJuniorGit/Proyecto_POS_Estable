using Core.DTOs;
using Core.Helpers;
using Core.Interfaces;

namespace Inventory.Module.Services;

/// <summary>
/// 8.182-T1 (S1/S2): servicio puro de redondeo comercial/psicológico con guarda de piso de margen.
/// Stateless y sin dependencias de persistencia: la misma instancia puede usarse desde el motor
/// bulk, las facturas de proveedor o hooks futuros del POS sin efectos colaterales.
/// 8.184-T1 (S3): el algoritmo se relocalizó a <see cref="PricingRoundingPolicy"/> (Core) para que
/// el cliente WPF calcule en vivo sin acoplarse a Inventory.Module; este servicio solo delega y
/// mantiene intacto el contrato IPricingRoundingPolicyService para los consumidores existentes.
/// </summary>
public sealed class PricingRoundingPolicyService : IPricingRoundingPolicyService
{
    public RoundingResult Round(decimal amount, RoundingRuleType rule, decimal cost, decimal? marginFloorPct, bool enforceFloor)
        => PricingRoundingPolicy.Round(amount, rule, cost, marginFloorPct, enforceFloor);

    public decimal? FindLowerStep(decimal amount, RoundingRuleType rule)
        => PricingRoundingPolicy.FindLowerStep(amount, rule);

    public RoundingResult? ProjectCashFriendlyBsS(
        decimal usdAmount,
        decimal exchangeRate,
        RoundingRuleType rule,
        decimal costUsd,
        decimal? marginFloorPct,
        bool enforceFloor)
        => PricingRoundingPolicy.ProjectCashFriendlyBsS(usdAmount, exchangeRate, rule, costUsd, marginFloorPct, enforceFloor);
}
