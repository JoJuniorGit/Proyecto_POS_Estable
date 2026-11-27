using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Helpers;
using Core.Interfaces;
using Moq;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.186-T3 (S2/S3): el checkout preview expone el USD EFECTIVO — inverso del Total Bs.S
/// efectivo publicado a la tasa anclada (helper único PricingCalculator.EffectiveUsdFromBsS,
/// ANEXO XII B1), garantía de cero desfase para el Gran Total $ del carrito web. El TotalUSD
/// canónico se conserva para gates/contratos internos.
/// </summary>
public class EffectiveUsdPreviewTests
{
    // =====================================================================
    // (a) Helper único de la inversa: 4dp AwayFromZero + fallbacks canónicos
    // =====================================================================

    [Fact]
    public void EffectiveUsdFromBsS_ReturnsFourDecimalInverseOfEffectiveBsS()
    {
        // 12.00 Bs.S efectivos a tasa 10 => 1.20 USD, no el canónico 1.15.
        Assert.Equal(1.2000m, PricingCalculator.EffectiveUsdFromBsS(12m, 10m, canonicalUsd: 1.15m));
    }

    [Fact]
    public void EffectiveUsdFromBsS_RoundsFifthDecimalAwayFromZero()
    {
        // 0.15005 con tasa 1 => el quinto decimal 5 sube: 0.1501 (AwayFromZero).
        Assert.Equal(0.1501m, PricingCalculator.EffectiveUsdFromBsS(0.15005m, 1m, canonicalUsd: 0.99m));
    }

    [Theory]
    [InlineData(0, 10)]   // sin Bs.S cobrado
    [InlineData(-5, 10)]  // Bs.S inválido
    [InlineData(12, 0)]   // sin tasa
    public void EffectiveUsdFromBsS_WithoutBsSOrWithoutRate_FallsBackToCanonicalUsd(decimal totalBsS, decimal rate)
    {
        Assert.Equal(1.15m, PricingCalculator.EffectiveUsdFromBsS(totalBsS, rate, canonicalUsd: 1.15m));
    }

    [Fact]
    public void EffectiveUsdFromBsS_CapturesRoundingSurplusFromCanonicalPrice()
    {
        // 0.81 USD * 842.21 = 682.1901 -> techo canónico 682.20; el inverso 4dp vuelve a 0.81.
        Assert.Equal(0.8100m, PricingCalculator.EffectiveUsdFromBsS(682.20m, 842.21m, canonicalUsd: 0.81m));
    }

    // =====================================================================
    // (b) Preview con regla cash VesCashFriendly: el efectivo manda (cero drift)
    // =====================================================================

    private static Mock<ISalesService> CreateSalesServiceMock(int saleId, decimal totalUsd, decimal rate, params SaleItemDto[] items)
    {
        var mock = new Mock<ISalesService>();
        mock.Setup(s => s.GetSaleAsync(saleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SaleDto
            {
                Id = saleId,
                TotalUSD = totalUsd,
                AppliedRate = rate,
                Items = items.ToList()
            });
        mock.Setup(s => s.ResolveCheckoutRateAsync(saleId, It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, decimal clientRate, CancellationToken _) => clientRate);
        return mock;
    }

    private static Mock<ISystemSettingsService> NeverRoundDownSettings(string value = "true")
    {
        var mock = new Mock<ISystemSettingsService>();
        mock.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        mock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.Pricing.DefaultEnforceMinimumMarginFloor))
            .ReturnsAsync(value);
        return mock;
    }

    private static SaleItemDto CashFriendlyItem(decimal unitPrice) => new()
    {
        ProductId = 1,
        ProductName = "Item",
        Quantity = 1m,
        UnitPrice = unitPrice,
        RoundingRuleCashBs = RoundingRuleType.VesCashFriendlyInteger
    };

    [Fact]
    public async Task CalculatePreviewAsync_CashFriendlySale_ExposesEffectiveTotalUsdAsExactInverseOfEffectiveBsS()
    {
        // 1.35 USD * 10 = 13.50 -> escalón entero con never-down => 14.00 Bs.S efectivos (canónico 1.35).
        var mock = CreateSalesServiceMock(70, totalUsd: 1.35m, rate: 10m, CashFriendlyItem(1.35m));
        var service = new CheckoutCalculationService(mock.Object, NeverRoundDownSettings().Object);

        var preview = await service.CalculatePreviewAsync(70, new CheckoutPreviewRequest
        {
            ExchangeRate = 10m,
            Payments = new List<SalePaymentDto>()
        });

        // Identidad de cero desfase: el USD efectivo es EXACTAMENTE el inverso 4dp del Bs.S devuelto.
        Assert.Equal(14.00m, preview.TotalBsS);
        Assert.Equal(Math.Round(preview.TotalBsS / 10m, 4, MidpointRounding.AwayFromZero), preview.EffectiveTotalUSD);
        // El escalón de la política es visible en USD (1.40, no el canónico 1.35).
        Assert.Equal(1.40m, preview.EffectiveTotalUSD);
        Assert.NotEqual(preview.TotalUSD, preview.EffectiveTotalUSD);
    }

    // =====================================================================
    // (c) Venta ExactMath/default: el USD efectivo coincide con el canónico
    // =====================================================================

    [Fact]
    public async Task CalculatePreviewAsync_ExactMathSale_EffectiveTotalUsdEqualsCanonicalUsd()
    {
        // Línea sin regla cash (ExactMath): techo canónico 8.104 — 0.81 * 842.21 = 682.1901 -> 682.20.
        var mock = CreateSalesServiceMock(71, totalUsd: 0.81m, rate: 842.21m,
            new SaleItemDto { ProductId = 1, ProductName = "Item", Quantity = 1m, UnitPrice = 0.81m });
        var service = new CheckoutCalculationService(mock.Object);

        var preview = await service.CalculatePreviewAsync(71, new CheckoutPreviewRequest
        {
            ExchangeRate = 842.21m,
            Payments = new List<SalePaymentDto>()
        });

        Assert.Equal(682.20m, preview.TotalBsS);
        Assert.Equal(0.81m, preview.TotalUSD);
        Assert.Equal(preview.TotalUSD, preview.EffectiveTotalUSD);
    }

    [Fact]
    public async Task CalculatePreviewAsync_SaleWithoutItems_EffectiveTotalUsdEqualsCanonicalUsd()
    {
        // Sin líneas rige el total canónico del contrato: 15 * 50 = 750 -> inverso exacto 15.
        var mock = CreateSalesServiceMock(72, totalUsd: 15m, rate: 50m);
        var service = new CheckoutCalculationService(mock.Object);

        var preview = await service.CalculatePreviewAsync(72, new CheckoutPreviewRequest
        {
            ExchangeRate = 50m,
            Payments = new List<SalePaymentDto>()
        });

        Assert.Equal(750m, preview.TotalBsS);
        Assert.Equal(preview.TotalUSD, preview.EffectiveTotalUSD);
    }

    [Fact]
    public async Task CalculatePreviewAsync_WithAnchoredClientRate_EffectiveTotalUsdUsesTheAnchoredRate()
    {
        // El cliente envía 60 y el resolver SEC-08 ancla a 50: el inverso debe usar la MISMA tasa
        // anclada que derivó el TotalBsS (con la tasa del cliente daría 750/60 = 12.5).
        var mock = CreateSalesServiceMock(74, totalUsd: 15m, rate: 50m);
        mock.Setup(s => s.ResolveCheckoutRateAsync(74, 60m, It.IsAny<CancellationToken>()))
            .ReturnsAsync(50m);
        var service = new CheckoutCalculationService(mock.Object);

        var preview = await service.CalculatePreviewAsync(74, new CheckoutPreviewRequest
        {
            ExchangeRate = 60m,
            Payments = new List<SalePaymentDto>()
        });

        Assert.Equal(750m, preview.TotalBsS);
        Assert.Equal(Math.Round(preview.TotalBsS / 50m, 4, MidpointRounding.AwayFromZero), preview.EffectiveTotalUSD);
        Assert.Equal(15m, preview.EffectiveTotalUSD);
    }

    // =====================================================================
    // (d) Los pagos del preview NO alteran el USD efectivo (solo el abono/restante)
    // =====================================================================

    [Fact]
    public async Task CalculatePreviewAsync_PaymentsDoNotAlterEffectiveTotalUsd()
    {
        var mock = CreateSalesServiceMock(73, totalUsd: 1.35m, rate: 10m, CashFriendlyItem(1.35m));
        var service = new CheckoutCalculationService(mock.Object, NeverRoundDownSettings().Object);

        var preview = await service.CalculatePreviewAsync(73, new CheckoutPreviewRequest
        {
            ExchangeRate = 10m,
            Payments = new List<SalePaymentDto>
            {
                new SalePaymentDto { PaymentMethodId = 1, Amount = 0m, AmountBsS = 5.00m }
            }
        });

        // Con o sin pagos el Gran Total $ efectivo es el mismo; el abono solo mueve restante/cobertura.
        Assert.Equal(14.00m, preview.TotalBsS);
        Assert.Equal(1.40m, preview.EffectiveTotalUSD);
        Assert.Equal(5.00m, preview.TotalPaidBsS);
        Assert.Equal(9.00m, preview.RemainingBalanceBsS);
        Assert.False(preview.IsFullyPaid);
    }

    // =====================================================================
    // (e) Pin de delegación: Sale.EffectiveTotalUSD usa el helper compartido
    // =====================================================================

    [Fact]
    public void Sale_EffectiveTotalUSD_DelegatesToSharedInverseHelper()
    {
        var sale = new Sale { TotalUSD = 1.15m, TotalBsS = 12m, AppliedRate = 10m };

        Assert.Equal(
            PricingCalculator.EffectiveUsdFromBsS(sale.TotalBsS, sale.AppliedRate, sale.TotalUSD),
            sale.EffectiveTotalUSD);
        Assert.Equal(1.2000m, sale.EffectiveTotalUSD);
    }

    [Fact]
    public void Sale_EffectiveTotalUSD_WithoutRateOrBsS_FallsBackToCanonical()
    {
        var withoutRate = new Sale { TotalUSD = 1.15m, TotalBsS = 12m, AppliedRate = 0m };
        var withoutBsS = new Sale { TotalUSD = 1.15m, TotalBsS = 0m, AppliedRate = 10m };

        Assert.Equal(1.15m, withoutRate.EffectiveTotalUSD);
        Assert.Equal(1.15m, withoutBsS.EffectiveTotalUSD);
    }
}
