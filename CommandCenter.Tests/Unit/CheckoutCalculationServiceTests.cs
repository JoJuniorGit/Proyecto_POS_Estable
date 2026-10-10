using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Moq;
using Sales.Module.DTOs;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.159-T1 (CLEAN-04, REQ-CHC-02): cobertura directa del servicio de cálculo extraído del
/// controlador. Pinea la matemática espejo (totales, pagos mixtos con conversión, redondeo
/// fiscal, vuelto), el anclaje de tasa SEC-08 ejercido vía ISalesService y los errores
/// preservados (missing → KeyNotFoundException; tasa final ≤ 0 → ArgumentException exacta).
/// </summary>
public class CheckoutCalculationServiceTests
{
    private const decimal AppliedRate = 50m;

    private static Mock<ISalesService> CreateSalesServiceMock(int saleId, decimal totalUsd, decimal appliedRate = AppliedRate)
    {
        var mock = new Mock<ISalesService>();
        mock.Setup(s => s.GetSaleAsync(saleId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new SaleDto
            {
                Id = saleId,
                TotalUSD = totalUsd,
                AppliedRate = appliedRate,
                Items = new List<SaleItemDto>()
            });
        return mock;
    }

    private static void SetupRatePassthrough(Mock<ISalesService> mock, int saleId)
    {
        mock.Setup(s => s.ResolveCheckoutRateAsync(saleId, It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int _, decimal clientRate, CancellationToken _) => clientRate);
    }

    [Fact]
    public async Task CalculatePreviewAsync_WithMixedUsdAndLocalPayments_ConvertsAndComputesChange()
    {
        // Arrange: venta de 15 USD a tasa 50; pago USD 20 + 1000 Bs.S (escenario del endpoint histórico)
        var mock = CreateSalesServiceMock(saleId: 42, totalUsd: 15m);
        SetupRatePassthrough(mock, 42);
        var service = new CheckoutCalculationService(mock.Object);

        var request = new CheckoutPreviewRequest
        {
            ExchangeRate = 50m,
            Payments = new List<SalePaymentDto>
            {
                new SalePaymentDto { PaymentMethodId = 1, Amount = 20m, AmountLocal = 1000m }
            }
        };

        // Act
        var preview = await service.CalculatePreviewAsync(42, request);

        // Assert
        Assert.Equal(15m, preview.TotalUSD);
        Assert.Equal(750m, preview.TotalBsS);
        Assert.Equal(20m, preview.TotalPaidUSD);
        Assert.Equal(1000m, preview.TotalPaidBsS);
        Assert.Equal(0m, preview.RemainingBalanceUSD);
        Assert.Equal(0m, preview.RemainingBalanceBsS);
        Assert.True(preview.IsFullyPaid);
        Assert.Equal(5m, preview.ChangeDueUSD);
        Assert.Equal(250m, preview.ChangeDueBsS);
    }

    [Fact]
    public async Task CalculatePreviewAsync_WithBsSOnlyPayment_ConvertsToUsdAtFourDecimals()
    {
        // Arrange: venta de 100 USD; pago de 2500 Bs.S sin USD → 50 USD equivalentes a 4 decimales
        var mock = CreateSalesServiceMock(saleId: 10, totalUsd: 100m);
        var service = new CheckoutCalculationService(mock.Object);

        var request = new CheckoutPreviewRequest
        {
            ExchangeRate = 0m,
            Payments = new List<SalePaymentDto>
            {
                new SalePaymentDto { PaymentMethodId = 2, Amount = 0m, AmountBsS = 2500m }
            }
        };

        // Act
        var preview = await service.CalculatePreviewAsync(10, request);

        // Assert: sin tasa cliente se usa AppliedRate y NO se invoca el resolver (anclaje omitido)
        Assert.Equal(50m, preview.TotalPaidUSD);
        Assert.Equal(2500m, preview.TotalPaidBsS);
        Assert.Equal(5000m, preview.TotalBsS);
        Assert.Equal(50m, preview.RemainingBalanceUSD);
        Assert.Equal(2500m, preview.RemainingBalanceBsS);
        Assert.False(preview.IsFullyPaid);
        mock.Verify(
            s => s.ResolveCheckoutRateAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task CalculatePreviewAsync_WithMixedPaymentsIncludingFractionalBsS_ComputesFiscalRoundingAdjustment()
    {
        // Arrange: pagos 50 + 50 USD con Bs.S 2500.00 + 2500.50 → residual físico 0.50 (redondeo fiscal)
        var mock = CreateSalesServiceMock(saleId: 42, totalUsd: 100m);
        SetupRatePassthrough(mock, 42);
        var service = new CheckoutCalculationService(mock.Object);

        var request = new CheckoutPreviewRequest
        {
            ExchangeRate = 50m,
            Payments = new List<SalePaymentDto>
            {
                new SalePaymentDto { PaymentMethodId = 1, Amount = 50m, AmountBsS = 2500m },
                new SalePaymentDto { PaymentMethodId = 2, Amount = 50m, AmountBsS = 2500.50m }
            }
        };

        // Act
        var preview = await service.CalculatePreviewAsync(42, request);

        // Assert
        Assert.Equal(100m, preview.TotalPaidUSD);
        Assert.Equal(5000.50m, preview.TotalPaidBsS);
        Assert.True(preview.IsFullyPaid);
        Assert.Equal(0.50m, preview.RoundingAdjustment);
    }

    [Fact]
    public async Task CalculatePreviewAsync_WithClientRate_UsesAnchoredRateFromResolverForAllTotals()
    {
        // Arrange: el cliente envía 60; el resolver SEC-08 ancla a la BCV 50
        var mock = CreateSalesServiceMock(saleId: 42, totalUsd: 100m);
        mock.Setup(s => s.ResolveCheckoutRateAsync(42, 60m, It.IsAny<CancellationToken>()))
            .ReturnsAsync(50m);
        var service = new CheckoutCalculationService(mock.Object);

        var request = new CheckoutPreviewRequest
        {
            ExchangeRate = 60m,
            Payments = new List<SalePaymentDto>
            {
                new SalePaymentDto { PaymentMethodId = 1, Amount = 100m }
            }
        };

        // Act
        var preview = await service.CalculatePreviewAsync(42, request);

        // Assert: todos los totales usan la tasa anclada, no la del cliente
        Assert.Equal(5000m, preview.TotalBsS);
        Assert.Equal(5000m, preview.TotalPaidBsS);
        Assert.True(preview.IsFullyPaid);
    }

    [Theory]
    [InlineData(15.05, 0, 0)]      // exactamente total + 0.05: sin vuelto
    [InlineData(15.06, 0.06, 3.00)] // un centavo por encima: vuelto USD convertido a Bs.S
    public async Task CalculatePreviewAsync_ChangeBoundary_UsesTotalPlusFiveCentsThreshold(
        decimal paidUsd,
        decimal expectedChangeUsd,
        decimal expectedChangeBsS)
    {
        // Arrange: venta de 15 USD; el umbral de vuelto es estricto (> total + 0.05)
        var mock = CreateSalesServiceMock(saleId: 8, totalUsd: 15m);
        var service = new CheckoutCalculationService(mock.Object);

        var request = new CheckoutPreviewRequest
        {
            ExchangeRate = 0m,
            Payments = new List<SalePaymentDto>
            {
                new SalePaymentDto { PaymentMethodId = 1, Amount = paidUsd }
            }
        };

        // Act
        var preview = await service.CalculatePreviewAsync(8, request);

        // Assert
        Assert.Equal(expectedChangeUsd, preview.ChangeDueUSD);
        Assert.Equal(expectedChangeBsS, preview.ChangeDueBsS);
    }

    [Fact]
    public async Task CalculatePreviewAsync_WhenPaymentsIsNull_ReturnsZeroPaidAndNotFullyPaid()
    {
        // Arrange: contrato con Payments nulo (el endpoint histórico toleraba el nulo sin iterar)
        var mock = CreateSalesServiceMock(saleId: 5, totalUsd: 20m);
        var service = new CheckoutCalculationService(mock.Object);

        var request = new CheckoutPreviewRequest { ExchangeRate = 0m, Payments = null! };

        // Act
        var preview = await service.CalculatePreviewAsync(5, request);

        // Assert
        Assert.Equal(0m, preview.TotalPaidUSD);
        Assert.Equal(0m, preview.TotalPaidBsS);
        Assert.Equal(20m, preview.RemainingBalanceUSD);
        Assert.False(preview.IsFullyPaid);
        Assert.Equal(0m, preview.RoundingAdjustment);
        Assert.Equal(0m, preview.ChangeDueUSD);
    }

    [Fact]
    public async Task CalculatePreviewAsync_WhenFinalRateIsZero_ThrowsArgumentExceptionWithExactMessage()
    {
        // Arrange: venta con AppliedRate inválida y sin tasa cliente → la tasa final es 0
        var mock = CreateSalesServiceMock(saleId: 7, totalUsd: 10m, appliedRate: 0m);
        var service = new CheckoutCalculationService(mock.Object);

        // Act
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => service.CalculatePreviewAsync(7, new CheckoutPreviewRequest { ExchangeRate = 0m }));

        // Assert: mensaje exacto histórico (400 por middleware de dominio)
        Assert.Equal("Tasa de cambio inválida.", ex.Message);
    }

    [Fact]
    public async Task CalculatePreviewAsync_WhenSaleDoesNotExist_PropagatesKeyNotFoundFromSalesService()
    {
        // Arrange: el fetch lanza como el servicio real (missing → 404 por middleware, sin null-check)
        var mock = new Mock<ISalesService>();
        mock.Setup(s => s.GetSaleAsync(99, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new KeyNotFoundException("Sale 99 not found."));
        var service = new CheckoutCalculationService(mock.Object);

        // Act
        var ex = await Assert.ThrowsAsync<KeyNotFoundException>(
            () => service.CalculatePreviewAsync(99, new CheckoutPreviewRequest { ExchangeRate = 50m }));

        // Assert: el mensaje del servicio real viaja intacto al middleware
        Assert.Equal("Sale 99 not found.", ex.Message);
    }
}
