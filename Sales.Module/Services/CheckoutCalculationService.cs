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

    public CheckoutCalculationService(ISalesService salesService, ISystemSettingsService? settingsService = null)
    {
        ArgumentNullException.ThrowIfNull(salesService);
        _salesService = salesService;
        _settingsService = settingsService;
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
        decimal totalBsS = await ResolveEffectiveTotalBsSAsync(sale, rate);

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

        return new CheckoutPreviewResponse
        {
            TotalUSD = totalUsd,
            TotalBsS = totalBsS,
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
    private async Task<decimal> ResolveEffectiveTotalBsSAsync(SaleDto sale, decimal rate)
    {
        var items = sale.Items;
        if (items == null || items.Count == 0)
        {
            return PricingCalculator.RoundToDigital(sale.TotalUSD * rate);
        }

        // El toggle se lee UNA vez y solo si alguna línea no-custom tiene política cash (misma
        // condición del completar venta); sin líneas cash el valor es irrelevante.
        bool neverRoundDown = true;
        if (items.Any(i => !i.IsCustomPrice && SalesService.IsCashFriendlyRule(i.RoundingRuleCashBs)))
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
                neverRoundDown);

            total += PricingCalculator.RoundToDigital(item.Quantity * unitPriceBsS);
        }

        return PricingCalculator.RoundToDigital(total);
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
