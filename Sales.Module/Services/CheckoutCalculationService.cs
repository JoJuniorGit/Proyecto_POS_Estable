using Core.Helpers;
using Sales.Module.DTOs;
using Sales.Module.Interfaces;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

/// <summary>
/// 8.159-T1 (CLEAN-04, REQ-CHC-01): servicio de dominio con la matemática espejo del
/// checkout-preview. Comportamiento observable idéntico al endpoint histórico: venta
/// inexistente → KeyNotFoundException (404 por middleware); tasa final ≤ 0 →
/// ArgumentException con mensaje exacto (400 por middleware de dominio).
/// </summary>
public class CheckoutCalculationService : ICheckoutCalculationService
{
    private readonly ISalesService _salesService;

    public CheckoutCalculationService(ISalesService salesService)
    {
        ArgumentNullException.ThrowIfNull(salesService);
        _salesService = salesService;
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
        decimal totalBsS = PricingCalculator.RoundToDigital(totalUsd * rate);

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

        bool isFullyPaid = remainingUsd <= 0.05m;
        decimal roundingAdjustment = remainingUsd <= 0.01m ? PricingCalculator.RoundToDigital(totalPaidBsS - totalBsS) : 0m;

        decimal changeUsd = 0m;
        decimal changeBsS = 0m;
        if (totalPaidUsd > totalUsd + 0.05m)
        {
            changeUsd = PricingCalculator.RoundToDigital(totalPaidUsd - totalUsd);
            changeBsS = PricingCalculator.ToBsS(changeUsd, rate);
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
}
