using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Receipts;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ReceiptsControllerTests
{
    private SalesDbContext CreateInMemorySalesDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    // 8.143: el controller lee el ajuste "CurrencyFormat" para el PDF; default Venezuelan.
    private static Moq.Mock<Core.Interfaces.ISystemSettingsService> CreateSettingsMock(string format = "Venezuelan")
    {
        var mock = new Moq.Mock<Core.Interfaces.ISystemSettingsService>();
        mock.Setup(s => s.GetSettingAsync("CurrencyFormat")).ReturnsAsync(format);
        return mock;
    }

    private static ReceiptsController CreateController(
        SalesDbContext db,
        Core.Interfaces.ICurrentUserService currentUserService,
        string format = "Venezuelan")
        => new ReceiptsController(
            new SalesReceiptService(db),
            new SaleReceiptRenderer(),
            currentUserService,
            CreateSettingsMock(format).Object);

    private static Sale BuildCompletedSale(int id, int invoiceNumber)
    {
        var sale = new Sale
        {
            Id = id,
            InvoiceNumber = invoiceNumber,
            Date = DateTime.UtcNow,
            Status = SaleStatus.Completed,
            PriceListType = "Retail",
            DeliveryStatus = SaleDeliveryStatus.Delivered,
            Subtotal = 10m,
            TotalUSD = 10m,
            TotalBsS = 730m,
            SubtotalBsS = 730m,
            AppliedRate = 73m,
            RoundingAdjustment = 0m,
            FinalPaidAmountBsS = 730m,
            CashierId = 1
        };
        sale.Items.Add(new SaleItem { ProductName = "Producto A", Quantity = 2m, UnitPrice = 5m, Subtotal = 10m, UnitPriceBsS = 365m, SubtotalBsS = 730m });
        sale.Payments.Add(new SalePayment { PaymentMethod = new PaymentMethod { Name = "Efectivo", IsCash = true }, Amount = 10m, AmountBsS = 730m, ExchangeRate = 73m });
        return sale;
    }

    private static void AttachUser(ReceiptsController controller, string userId, string? role = null)
    {
        var claims = new System.Collections.Generic.List<System.Security.Claims.Claim>
        {
            new(System.Security.Claims.ClaimTypes.NameIdentifier, userId)
        };
        if (role != null)
        {
            claims.Add(new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.Role, role));
        }

        var user = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(claims, "mock"));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext { User = user }
        };
    }

    [Fact]
    public async Task GetReceipt_ForCompletedSale_ReturnsPdfFile()
    {
        using var db = CreateInMemorySalesDbContext();
        var sale = BuildCompletedSale(42, 1001);
        db.Sales.Add(sale);
        await db.SaveChangesAsync();

        var currentUserServiceMock = new Moq.Mock<Core.Interfaces.ICurrentUserService>();
        currentUserServiceMock.Setup(c => c.UserId).Returns("1");

        var controller = CreateController(db, currentUserServiceMock.Object);
        AttachUser(controller, "1");

        var result = await controller.GetReceipt(42, CancellationToken.None);

        var fileContentResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", fileContentResult.ContentType);
        Assert.NotNull(fileContentResult.FileContents);
        Assert.NotEmpty(fileContentResult.FileContents);
        var header = Encoding.ASCII.GetString(fileContentResult.FileContents.AsSpan(0, 5).ToArray());
        Assert.Equal("%PDF-", header);
    }

    [Fact]
    public async Task GetReceipt_WithVenezuelanSetting_PdfUsesEsVeNumberFormat()
    {
        using var db = CreateInMemorySalesDbContext();
        db.Sales.Add(BuildCompletedSale(45, 1004));
        await db.SaveChangesAsync();

        var currentUserServiceMock = new Moq.Mock<Core.Interfaces.ICurrentUserService>();
        currentUserServiceMock.Setup(c => c.UserId).Returns("1");

        var controller = CreateController(db, currentUserServiceMock.Object, "Venezuelan");
        AttachUser(controller, "1");

        var result = await controller.GetReceipt(45, CancellationToken.None);

        var fileContentResult = Assert.IsType<FileContentResult>(result);
        var content = Encoding.ASCII.GetString(fileContentResult.FileContents!);
        Assert.Contains("730,00", content);
        Assert.Contains("73,0000", content);
        Assert.DoesNotContain("730.00", content);
    }

    [Fact]
    public async Task GetReceipt_WithInternationalSetting_PdfUsesInvariantNumberFormat()
    {
        using var db = CreateInMemorySalesDbContext();
        db.Sales.Add(BuildCompletedSale(46, 1005));
        await db.SaveChangesAsync();

        var currentUserServiceMock = new Moq.Mock<Core.Interfaces.ICurrentUserService>();
        currentUserServiceMock.Setup(c => c.UserId).Returns("1");

        var controller = CreateController(db, currentUserServiceMock.Object, "International");
        AttachUser(controller, "1");

        var result = await controller.GetReceipt(46, CancellationToken.None);

        var fileContentResult = Assert.IsType<FileContentResult>(result);
        var content = Encoding.ASCII.GetString(fileContentResult.FileContents!);
        Assert.Contains("730.00", content);
        Assert.Contains("73.0000", content);
        Assert.DoesNotContain("730,00", content);
    }

    [Fact]
    public async Task GetReceipt_ForCompletedSale_OtherCashier_ReturnsForbidden()
    {
        using var db = CreateInMemorySalesDbContext();
        var sale = new Sale
        {
            Id = 43,
            InvoiceNumber = 1002,
            Status = SaleStatus.Completed,
            CashierId = 1
        };
        db.Sales.Add(sale);
        await db.SaveChangesAsync();

        var currentUserServiceMock = new Moq.Mock<Core.Interfaces.ICurrentUserService>();
        currentUserServiceMock.Setup(c => c.UserId).Returns("2");

        var controller = CreateController(db, currentUserServiceMock.Object);
        AttachUser(controller, "2", "Cashier");

        var result = await controller.GetReceipt(43, CancellationToken.None);

        var statusCodeResult = Assert.IsType<ObjectResult>(result);
        Assert.Equal(403, statusCodeResult.StatusCode);
    }

    [Fact]
    public async Task GetReceipt_ForCompletedSale_Admin_ReturnsPdfFile()
    {
        using var db = CreateInMemorySalesDbContext();
        var sale = new Sale
        {
            Id = 44,
            InvoiceNumber = 1003,
            Status = SaleStatus.Completed,
            CashierId = 1
        };
        db.Sales.Add(sale);
        await db.SaveChangesAsync();

        var currentUserServiceMock = new Moq.Mock<Core.Interfaces.ICurrentUserService>();
        currentUserServiceMock.Setup(c => c.UserId).Returns("99");

        var controller = CreateController(db, currentUserServiceMock.Object);
        AttachUser(controller, "99", "Admin");

        var result = await controller.GetReceipt(44, CancellationToken.None);

        Assert.IsType<FileContentResult>(result);
    }

    [Fact]
    public async Task GetReceipt_ForMissingSale_ReturnsNotFound()
    {
        using var db = CreateInMemorySalesDbContext();
        var controller = CreateController(db, new Moq.Mock<Core.Interfaces.ICurrentUserService>().Object);

        var result = await controller.GetReceipt(999, CancellationToken.None);

        var notFoundResult = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Equal(404, notFoundResult.StatusCode);
    }
}
