using Core.DTOs;

namespace Desktop.Client.Messages;

/// <summary>
/// 8.185-T4 (S2c): override de redondeo cash de un artículo recién guardado en el modal de
/// Catálogo (null = heredar la política global). Catálogo y POS recomputan el Bs.S efectivo del
/// ítem afectado en memoria — 0 consultas — sin re-ejecutar búsquedas.
/// </summary>
public sealed class ProductRoundingChangedMessage
{
    public ProductRoundingChangedMessage(int productId, RoundingRuleType? roundingRuleCashBs)
    {
        ProductId = productId;
        RoundingRuleCashBs = roundingRuleCashBs;
    }

    /// <summary>Id del artículo cuyo override cambió (los ítems con otro id quedan intactos).</summary>
    public int ProductId { get; }

    /// <summary>Override cash persistido: null = heredar la política global vigente.</summary>
    public RoundingRuleType? RoundingRuleCashBs { get; }
}
