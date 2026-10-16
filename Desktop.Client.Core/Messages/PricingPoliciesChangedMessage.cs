using CommunityToolkit.Mvvm.Messaging.Messages;
using Core.DTOs;

namespace Desktop.Client.Messages;

/// <summary>
/// 8.185-T4 (S2b): políticas globales de precios y redondeo recién persistidas desde Ajustes.
/// Se emite SOLO tras un PUT exitoso (el fallo revierte la UI y no propaga nada) y viaja con el
/// bloque completo para que Catálogo y POS apliquen la regla/toggle en memoria — 0 consultas —
/// y recomputen sus displays al instante (molde <c>ExchangeRateChangedMessage</c>).
/// </summary>
public sealed class PricingPoliciesChangedMessage : ValueChangedMessage<PricingPoliciesDto>
{
    public PricingPoliciesChangedMessage(PricingPoliciesDto value) : base(value)
    {
    }
}
