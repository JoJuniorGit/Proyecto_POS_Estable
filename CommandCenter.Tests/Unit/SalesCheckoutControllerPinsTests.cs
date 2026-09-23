using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Core.DTOs;
using Core.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Sales.Module.DTOs;
using Sales.Module.Interfaces;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class SalesCheckoutControllerPinsTests
{
    private static SalesController CreateController(Mock<ISalesService> salesService, string userId = "7")
    {
        var mockUser = new Mock<ICurrentUserService>();
        mockUser.Setup(u => u.UserId).Returns(userId);

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["Idempotency-Key"] = "AUD13-CHECKOUT-PIN";
        httpContext.Request.Path = "/api/sales/1/complete";
        httpContext.Request.Method = "POST";

        return new SalesController(salesService.Object, mockUser.Object)
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext }
        };
    }

    private static Mock<ISalesService> CreateSalesServiceMock(int invoiceNumber, System.Action<int?, IEnumerable<PaymentInfo>> capture)
    {
        var mock = new Mock<ISalesService>();
        mock.Setup(s => s.CompleteSaleAsync(
                It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<IEnumerable<PaymentInfo>>(), It.IsAny<decimal>(),
                It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<byte[]?>(),
                It.IsAny<CancellationToken>(), It.IsAny<int?>()))
            .Callback<int, decimal, IEnumerable<PaymentInfo>, decimal, int?, bool, string?, byte[]?, CancellationToken, int?>(
                (_, _, payments, _, cashierId, _, _, _, _, _) => capture(cashierId, payments))
            .ReturnsAsync(invoiceNumber);
        return mock;
    }

    [Fact]
    public async Task CompleteSale_WhenPaymentsProvided_MapsMethodAmountsAndReferenceIntoPaymentInfos()
    {
        IEnumerable<PaymentInfo>? capturedPayments = null;
        var mockSales = CreateSalesServiceMock(515, (_, payments) => capturedPayments = payments);
        var controller = CreateController(mockSales);

        var request = new CompleteSaleRequest
        {
            ExchangeRate = 45m,
            Payments = new List<SalePaymentDto>
            {
                new() { PaymentMethodId = 3, Amount = 12.34m, AmountBsS = 555.30m, ReferenceNumber = "REF-PIN-1" }
            }
        };

        var response = await controller.CompleteSale(1, request);

        var ok = Assert.IsType<OkObjectResult>(response);
        Assert.Equal(515, ok.Value);
        var payment = Assert.Single(capturedPayments!);
        Assert.Equal(3, payment.PaymentMethodId);
        Assert.Equal(12.34m, payment.Amount);
        Assert.Equal(555.30m, payment.AmountLocal);
        Assert.Equal("REF-PIN-1", payment.Reference);
    }

    [Fact]
    public async Task CompleteSale_WhenAuthenticatedUserIdParses_UsesItOverRequestCashierId()
    {
        int? capturedCashierId = null;
        var mockSales = CreateSalesServiceMock(516, (cashierId, _) => capturedCashierId = cashierId);
        var controller = CreateController(mockSales, userId: "7");

        var request = new CompleteSaleRequest
        {
            ExchangeRate = 45m,
            CashierId = 99,
            Payments = new List<SalePaymentDto>()
        };

        await controller.CompleteSale(1, request);

        Assert.Equal(7, capturedCashierId);
    }

    [Fact]
    public async Task CompleteSale_WhenUserHasDriverRole_ReturnsForbiddenWithoutInvokingService()
    {
        var mockSales = CreateSalesServiceMock(517, (_, _) => { });
        var controller = CreateController(mockSales);
        controller.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            new[] { new Claim(ClaimTypes.Role, "Driver") }, "TestAuth"));

        var response = await controller.CompleteSale(1, new CompleteSaleRequest { ExchangeRate = 45m });

        var forbidden = Assert.IsType<ObjectResult>(response);
        Assert.Equal(StatusCodes.Status403Forbidden, forbidden.StatusCode);
        mockSales.Verify(s => s.CompleteSaleAsync(
            It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<IEnumerable<PaymentInfo>>(), It.IsAny<decimal>(),
            It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<byte[]?>(),
            It.IsAny<CancellationToken>(), It.IsAny<int?>()), Times.Never);
    }
}
