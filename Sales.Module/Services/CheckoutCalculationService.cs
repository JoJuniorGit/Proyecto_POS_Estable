using Core.Common;
using Core.DTOs;
using Core.Helpers;
using Core.Interfaces;
using Sales.Module.DTOs;
using Sales.Module.Interfaces;
using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

/// <summary>
/// 8.159-T1 (CLEAN-04, REQ-CHC-01): servicio de dominio con la matemática espejo del
/// checkout-preview. Comportamiento observable idéntico al endpoint histórico: venta
/// inexistente → KeyNotFoundException (404 por middleware); tasa final ≤ 0 →
/// ArgumentException con mensaje exacto (400 por middleware de dominio).
/// 8.185-T3 (S5): el total Bs.S del preview es el EFECTIVO por línea (misma derivación del
/// completar venta) y la cobertura replica el gate dual-dominio.
/// </summary>
public class CheckoutCalculationService : ICheckoutCalculationService
{
    private readonly ISalesService _salesService;
    private readonly ISystemSettingsService? _settingsService;
    // 8.211-T3c3c1 (S5): the preview mirrors the active pricing currency so VES-only cash rules
    // are not applied to a Dual non-VES secondary or Single profile. Null keeps legacy USD/VES.
    private readonly IMonetaryProfileService? _monetaryProfileService;

    public CheckoutCalculationService(
        ISalesService salesService,
        ISystemSettingsService? settingsService = null,
        IMonetaryProfileService? monetaryProfileService = null)
    {
        ArgumentNullException.ThrowIfNull(salesService);
        _salesService = salesService;
        _settingsService = settingsService;
        _monetaryProfileService = monetaryProfileService;
    }

    public async Task<CheckoutPreviewResponse> CalculatePreviewAsync(int saleId, CheckoutPreviewRequest request, CancellationToken cancellationToken = default)
    {
        // Missing: GetSaleAsync lanza KeyNotFoundException("Sale {id} not found.") → 404 por
        // middleware de dominio (comportamiento real histórico, sin null-check del controller).
        var sale = await _salesService.GetSaleAsync(saleId, cancellationToken);

        // 8.152 (SEC-08): con tasa del cliente se aplica el MISMO anclaje del completar venta
        // (el ArgumentException del rechazo ±100% fluye al middleware global → 400).
        decimal rate = request.ExchangeRate > 0
            ? await _salesService.ResolveCheckoutRateAsync(saleId, request.ExchangeRate, cancellationToken)
            : sale.AppliedRate;
        if (rate <= 0)
        {
            throw new ArgumentException("Tasa de cambio inválida.");
        }

        decimal totalUsd = sale.TotalUSD;

        // 8.185-T3 (S5): total Bs.S EFECTIVO — derivación por línea idéntica a la del completar
        // venta (regla snapshot + toggle vigente). Sin líneas rige el total canónico del contrato.
        // 8.211-T3c3c1 (S5): las reglas cash-friendly solo aplican cuando la moneda de precio es VES.
        bool useVesCashRules = await ResolveUsesVesCashRulesAsync(sale, cancellationToken);
        decimal totalBsS = await ResolveEffectiveTotalBsSAsync(sale, rate, useVesCashRules, cancellationToken);

        decimal totalPaidUsd = 0m;
        decimal totalPaidBsS = 0m;

        if (request.Payments != null)
        {
            foreach (var p in request.Payments)
            {
                decimal pUsd = p.Amount;
                decimal pBsS = p.AmountBsS > 0 ? p.AmountBsS : p.AmountLocal;

                if (pUsd <= 0 && pBsS > 0 && rate > 0)
                {
                    pUsd = PricingCalculator.ToUSD(pBsS, rate, decimals: 4);
                }
                else if (pBsS <= 0 && pUsd > 0 && rate > 0)
                {
                    pBsS = PricingCalculator.ToBsS(pUsd, rate);
                }

                totalPaidUsd += PricingCalculator.RoundToDigital(pUsd);
                totalPaidBsS += PricingCalculator.RoundToDigital(pBsS);
            }
        }

        decimal remainingUsd = Math.Max(0m, totalUsd - totalPaidUsd);
        decimal remainingBsS = Math.Max(0m, totalBsS - totalPaidBsS);

        // 8.185-T3 (S5): espejo del gate dual-dominio del completar venta — la venta queda cubierta
        // si sobra tolerancia (0.05 USD por dominio) en al menos uno de los dos dominios.
        bool isFullyPaid = !(remainingUsd > 0.05m && remainingBsS > 0.05m * rate);
        decimal roundingAdjustment = remainingUsd <= 0.01m ? PricingCalculator.RoundToDigital(totalPaidBsS - totalBsS) : 0m;

        // 8.185-T3 (decisión mantenedor): vuelto DUAL espejo del completar venta — el exceso se mide
        // contra el total EFECTIVO en el dominio Bs.S. Exacto al efectivo => sin vuelto (el escalón
        // de la política no se devuelve); por encima => SOLO el exceso contra el efectivo. Con
        // ExactMath (efectivo == canónico) los montos coinciden con el contrato previo.
        decimal changeUsd = 0m;
        decimal changeBsS = 0m;
        decimal paidBsSExcess = PricingCalculator.RoundToDigital(totalPaidBsS - totalBsS);
        if (paidBsSExcess > 0.05m * rate)
        {
            changeBsS = paidBsSExcess;
            changeUsd = PricingCalculator.ToUSD(changeBsS, rate);
        }

        // 8.186-T3 (S2): el USD efectivo es el inverso del MISMO TotalBsS efectivo devuelto, con
        // la MISMA tasa del preview (garantía: EffectiveTotalUSD == round(TotalBsS / rate, 4));
        // el canónico queda como fallback y sigue rigiendo gates/contratos internos.
        return new CheckoutPreviewResponse
        {
            TotalUSD = totalUsd,
            TotalBsS = totalBsS,
            EffectiveTotalUSD = PricingCalculator.EffectiveUsdFromBsS(totalBsS, rate, totalUsd),
            TotalPaidUSD = totalPaidUsd,
            TotalPaidBsS = totalPaidBsS,
            RemainingBalanceUSD = remainingUsd,
            RemainingBalanceBsS = remainingBsS,
            RoundingAdjustment = roundingAdjustment,
            ChangeDueUSD = changeUsd,
            ChangeDueBsS = changeBsS,
            IsFullyPaid = isFullyPaid
        };
    }

    /// <summary>
    /// 8.185-T3 (S5): total Bs.S efectivo del preview. Espeja RecalculateTotalAsync: por línea
    /// aplica la regla cash snapshot (override resuelto al agregar) con el toggle vigente y cae al
    /// techo canónico 8.104 para reglas no-cash, líneas custom o líneas previas a 8.185.
    /// </summary>
    private async Task<decimal> ResolveEffectiveTotalBsSAsync(
        SaleDto sale,
        decimal rate,
        bool useVesCashRules,
        CancellationToken cancellationToken)
    {
        var items = sale.Items;
        if (items == null || items.Count == 0)
        {
            return PricingCalculator.RoundToDigital(sale.TotalUSD * rate);
        }

        // El toggle se lee UNA vez y solo si la moneda de precio es VES y alguna línea no-custom
        // tiene política cash (misma condición del completar venta); sin reglas VES es irrelevante.
        bool neverRoundDown = true;
        if (useVesCashRules
            && items.Any(i => !i.IsCustomPrice && SalesService.IsCashFriendlyRule(i.RoundingRuleCashBs)))
        {
            neverRoundDown = await ResolveNeverRoundDownAsync();
        }

        decimal total = 0m;
        foreach (var item in items)
        {
            decimal unitPriceBsS = SalesService.ApplyCashRoundingPolicy(
                item.UnitPrice,
                item.IsCustomPrice,
                item.RoundingRuleCashBs,
                rate,
                neverRoundDown,
                useVesCashRules);

            total += PricingCalculator.RoundToDigital(item.Quantity * unitPriceBsS);
        }

        return PricingCalculator.RoundToDigital(total);
    }

    /// <summary>
    /// 8.211-T3c3c1 (S5): pricing currency for the preview. Single ⇒ no VES rules; Dual ⇒
    /// normalized secondary; a profile read failure falls back to the sale snapshot (null does
    /// not apply VES rules). Null profile service preserves the legacy USD/VES behavior.
    /// </summary>
    private async Task<bool> ResolveUsesVesCashRulesAsync(SaleDto sale, CancellationToken cancellationToken)
    {
        if (_monetaryProfileService is null)
        {
            // Legacy compositions: Single never applies VES rules; Dual USD/VES keeps them.
            return ResolveUsesVesCashRulesFromSnapshot(sale);
        }

        try
        {
            var profile = await _monetaryProfileService.GetAsync(cancellationToken);
            if (profile.Mode != MonetaryMode.Dual)
            {
                return false;
            }

            var secondary = CurrencyCodeNormalizer.NormalizeToIso(profile.SecondaryCurrencyCode) ?? "VES";
            return string.Equals(secondary, "VES", StringComparison.Ordinal);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return ResolveUsesVesCashRulesFromSnapshot(sale);
        }
    }

    private static bool ResolveUsesVesCashRulesFromSnapshot(SaleDto sale)
    {
        if (string.Equals(sale.MonetaryMode, "Single", StringComparison.Ordinal))
        {
            return false;
        }

        var snapshot = CurrencyCodeNormalizer.NormalizeToIso(sale.SecondaryCurrencyCode);
        return string.Equals(snapshot, "VES", StringComparison.Ordinal);
    }

    /// <summary>Toggle vigente del nunca-hacia-abajo (default true), mismo contrato del completar venta.</summary>
    private async Task<bool> ResolveNeverRoundDownAsync()
    {
        if (_settingsService == null)
        {
            return true;
        }

        var value = await _settingsService.GetSettingAsync(Core.Constants.SettingKeys.Pricing.DefaultEnforceMinimumMarginFloor);
        return SalesService.ResolveNeverRoundDownFrom(value);
    }
}
