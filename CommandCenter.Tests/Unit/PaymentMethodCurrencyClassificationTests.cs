using Backend.API.Controllers;
using Core.Helpers;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class PaymentMethodCurrencyClassificationTests
{
    private static SalesDbContext CreateInMemorySalesContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: System.Guid.NewGuid().ToString())
            .Options;
        return new SalesDbContext(options);
    }

    private static InventoryDbContext CreateInMemoryInventoryContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: System.Guid.NewGuid().ToString())
            .Options;
        return new InventoryDbContext(options);
    }

    [Fact]
    public async Task GetReportById_Dolares_ClasificaBsSPorResolver()
    {
        var salesCtx = CreateInMemorySalesContext();
        var inventoryCtx = CreateInMemoryInventoryContext();

        var closure = new DailyClosure
        {
            Id = 1,
            UserId = "TestCashier",
            ExchangeRate = 3600m,
            ClosureDate = System.DateTime.UtcNow,
            TotalExpectedBsS = 21600m,
            TotalActualBsS = 21600m,
            TotalDifferenceBsS = 0m,
            Details = new List<ClosureDetail>
            {
                new()
                {
                    PaymentMethodId = 1,
                    PaymentMethodName = "Dolares",
                    ExpectedAmountBsS = 10800m,
                    ActualAmountBsS = 10800m,
                    DifferenceBsS = 0m
                },
                new()
                {
                    PaymentMethodId = 2,
                    PaymentMethodName = "Divisas (USD)",
                    ExpectedAmountBsS = 10800m,
                    ActualAmountBsS = 10800m,
                    DifferenceBsS = 0m
                }
            }
        };
        salesCtx.DailyClosures.Add(closure);
        await salesCtx.SaveChangesAsync();

        var closureService = new Mock<IDailyClosureService>();
        closureService.Setup(s => s.GetClosureAsync(1)).ReturnsAsync(ShiftReportMapper.MapClosure(closure));

        var controller = new ShiftsController(
            closureService.Object,
            new Mock<ICurrentUserService>().Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.Role, "Admin")
                }, "test"))
            }
        };

        var result = await controller.GetReportById(1, CancellationToken.None);

        var okResult = Assert.IsType<OkObjectResult>(result);
        var report = Assert.IsType<ShiftReportDto>(okResult.Value);
        Assert.Equal(2, report.Details.Count);

        var dolares = report.Details.First(d => d.PaymentMethodName == "Dolares");
        Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, dolares.Currency);
        Assert.Equal(10800m, dolares.SystemAmount);

        var divisas = report.Details.First(d => d.PaymentMethodName == "Divisas (USD)");
        Assert.Equal(PaymentMethodCurrencyResolver.Usd, divisas.Currency);
        Assert.Equal(3.00m, divisas.SystemAmount);
    }

    [Fact]
    public async Task GetReportById_And_ClosureReceipt_ClasificanIgual_MismoCierre()
    {
        var salesCtx = CreateInMemorySalesContext();
        var inventoryCtx = CreateInMemoryInventoryContext();

        var closure = new DailyClosure
        {
            Id = 42,
            UserId = "TestCashier",
            ExchangeRate = 3600m,
            ClosureDate = System.DateTime.UtcNow,
            TotalExpectedBsS = 21600m,
            TotalActualBsS = 21600m,
            TotalDifferenceBsS = 0m,
            Details = new List<ClosureDetail>
            {
                new()
                {
                    PaymentMethodId = 1,
                    PaymentMethodName = "Dolares",
                    ExpectedAmountBsS = 10800m,
                    ActualAmountBsS = 10800m,
                    DifferenceBsS = 0m
                },
                new()
                {
                    PaymentMethodId = 2,
                    PaymentMethodName = "Divisas (USD)",
                    ExpectedAmountBsS = 10800m,
                    ActualAmountBsS = 10800m,
                    DifferenceBsS = 0m
                }
            }
        };
        salesCtx.DailyClosures.Add(closure);
        await salesCtx.SaveChangesAsync();

        var closureService = new Mock<IDailyClosureService>();
        closureService.Setup(s => s.GetClosureAsync(42)).ReturnsAsync(ShiftReportMapper.MapClosure(closure));

        var controller = new ShiftsController(
            closureService.Object,
            new Mock<ICurrentUserService>().Object);

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(new[]
                {
                    new Claim(ClaimTypes.Role, "Admin")
                }, "test"))
            }
        };

        var reportResult = await controller.GetReportById(42, CancellationToken.None);
        var reportOk = Assert.IsType<OkObjectResult>(reportResult);
        var report = Assert.IsType<ShiftReportDto>(reportOk.Value);

        string receipt = DailyClosureService.GenerateReceiptContent(ShiftReportMapper.MapClosure(closure), isBlind: false);

        var reportByMethod = report.Details.ToDictionary(d => d.PaymentMethodName);

        foreach (var detail in closure.Details)
        {
            string expectedCurrency = PaymentMethodCurrencyResolver.Resolve(detail.PaymentMethodName);

            Assert.True(reportByMethod.ContainsKey(detail.PaymentMethodName),
                $"Report missing payment method: {detail.PaymentMethodName}");
            Assert.Equal(expectedCurrency, reportByMethod[detail.PaymentMethodName].Currency);

            Assert.Contains(detail.PaymentMethodName, receipt);
            string[] receiptLines = receipt.Split('\n');
            bool foundInReceipt = false;
            foreach (string line in receiptLines)
            {
                if (line.Contains(detail.PaymentMethodName) && line.Contains(expectedCurrency))
                {
                    foundInReceipt = true;
                    break;
                }
            }
            Assert.True(foundInReceipt,
                $"Receipt missing payment method {detail.PaymentMethodName} with currency {expectedCurrency}");
        }
    }

    [Fact]
    public void Resolve_DolarSinAcento_RetornaLocalCurrency()
    {
        string result = PaymentMethodCurrencyResolver.Resolve("Dolares");
        Assert.Equal(PaymentMethodCurrencyResolver.LocalCurrency, result);
    }

    [Fact]
    public void Resolve_DivisasUSD_RetornaUSD()
    {
        string result = PaymentMethodCurrencyResolver.Resolve("Divisas (USD)");
        Assert.Equal(PaymentMethodCurrencyResolver.Usd, result);
    }

    [Fact]
    public void ToUSD_RoundsAwayFromZero_DiffersFromRawDivision()
    {
        decimal result = PricingCalculator.ToUSD(100m, 6m);
        decimal rawDivision = 100m / 6m;

        Assert.Equal(16.67m, result);
        Assert.NotEqual(rawDivision, result);

        Assert.Equal(0.13m, PricingCalculator.ToUSD(1m, 8m));
    }
}
