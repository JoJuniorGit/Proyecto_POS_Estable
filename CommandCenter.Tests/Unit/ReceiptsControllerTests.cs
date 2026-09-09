using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

    [Fact]
    public async Task GetReceipt_ForCompletedSale_ReturnsPdfFile()
    {
        using var db = CreateInMemorySalesDbContext();
        var sale = new Sale
        {
            Id = 42,
            InvoiceNumber = 1001,
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
            FinalPaidAmountBsS = 730m
        };
        sale.Items.Add(new SaleItem { ProductName = "Producto A", Quantity = 2m, UnitPrice = 5m, Subtotal = 10m, UnitPriceBsS = 365m, SubtotalBsS = 730m });
        sale.Payments.Add(new SalePayment { PaymentMethod = new PaymentMethod { Name = "Efectivo", IsCash = true }, Amount = 10m, AmountBsS = 730m, ExchangeRate = 73m });
        db.Sales.Add(sale);
        await db.SaveChangesAsync();

        var controller = new ReceiptsController(db, new SaleReceiptRenderer());

        var result = await controller.GetReceipt(42, CancellationToken.None);

        var fileContentResult = Assert.IsType<FileContentResult>(result);
        Assert.Equal("application/pdf", fileContentResult.ContentType);
        Assert.NotNull(fileContentResult.FileContents);
        Assert.NotEmpty(fileContentResult.FileContents);
        var header = Encoding.ASCII.GetString(fileContentResult.FileContents.AsSpan(0, 5).ToArray());
        Assert.Equal("%PDF-", header);
    }

    [Fact]
    public async Task GetReceipt_ForMissingSale_ReturnsNotFound()
    {
        using var db = CreateInMemorySalesDbContext();
        var controller = new ReceiptsController(db, new SaleReceiptRenderer());

        var result = await controller.GetReceipt(999, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }
}