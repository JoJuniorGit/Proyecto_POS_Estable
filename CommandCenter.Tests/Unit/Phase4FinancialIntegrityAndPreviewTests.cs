using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Core.DTOs;
using Core.Helpers;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Sales.Module.DTOs;
using Sales.Module.Interfaces;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class Phase4FinancialIntegrityAndPreviewTests
{
    [Fact]
    public void PricingCalculator_ToUSD_SupportsConfigurablePrecision()
    {
        decimal amountBsS = 10m;
        decimal exchangeRate = 36.42m;

        // Default 2 decimals
        decimal defaultResult = PricingCalculator.ToUSD(amountBsS, exchangeRate);
        Assert.Equal(0.27m, defaultResult);

        // 4 decimals high precision [8C-M1]
        decimal highPrecisionResult = PricingCalculator.ToUSD(amountBsS, exchangeRate, decimals: 4);
        Assert.Equal(0.2746m, highPrecisionResult);
    }

    [Fact]
    public async Task SalesController_GetCheckoutPreview_ComputesAuthoritativeTotalsAndChange()
    {
        // Arrange
        var mockSalesService = new Mock<ISalesService>();
        var mockCurrentUserService = new Mock<Core.Interfaces.ICurrentUserService>();

        var saleDto = new SaleDto
        {
            Id = 42,
            TotalUSD = 15.00m,
            AppliedRate = 50.00m,
            Items = new List<SaleItemDto>()
        };

        mockSalesService.Setup(s => s.GetSaleAsync(42)).ReturnsAsync(saleDto);

        var controller = new SalesController(mockSalesService.Object, mockCurrentUserService.Object, null);

        var previewRequest = new CheckoutPreviewRequest
        {
            ExchangeRate = 50.00m,
            Payments = new List<SalePaymentDto>
            {
                new SalePaymentDto
                {
                    PaymentMethodId = 1, // USD Cash
                    Amount = 20.00m,
                    AmountLocal = 1000.00m
                }
            }
        };

        // Act
        var actionResult = await controller.GetCheckoutPreview(42, previewRequest);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var preview = Assert.IsType<CheckoutPreviewResponse>(okResult.Value);

        Assert.Equal(15.00m, preview.TotalUSD);
        Assert.Equal(750.00m, preview.TotalBsS);
        Assert.Equal(20.00m, preview.TotalPaidUSD);
        Assert.Equal(1000.00m, preview.TotalPaidBsS);
        Assert.Equal(0m, preview.RemainingBalanceUSD);
        Assert.Equal(0m, preview.RemainingBalanceBsS);
        Assert.True(preview.IsFullyPaid);
        Assert.Equal(5.00m, preview.ChangeDueUSD); // Vuelto de $5 USD
        Assert.Equal(250.00m, preview.ChangeDueBsS); // Vuelto de Bs. 250
    }
}
