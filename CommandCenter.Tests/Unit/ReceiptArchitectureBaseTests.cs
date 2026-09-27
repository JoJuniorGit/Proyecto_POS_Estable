using System;
using System.Linq;
using Sales.Module.Entities;
using Sales.Module.Receipts;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ReceiptArchitectureBaseTests
{
    [Fact]
    public void StubReceiptRenderer_WhenInvoked_ThrowsNotImplementedException()
    {
        var renderer = new StubReceiptRenderer();
        var context = new SaleReceiptContext { SaleId = 1 };

        var ex = Assert.Throws<NotImplementedException>(() => renderer.Render(context));

        Assert.Contains("siguiente fase", ex.Message);
    }

    [Fact]
    public void SaleReceiptContext_CreateFromSale_SnapshotsImmutableFields()
    {
        var sale = new Sale
        {
            Id = 7,
            InvoiceNumber = 90210,
            Date = new DateTime(2026, 9, 9, 12, 0, 0, DateTimeKind.Utc),
            CustomerName = "Cliente Prueba",
            CustomerCedula = "V-12345678",
            Subtotal = 10m,
            TotalUSD = 10m,
            TotalBsS = 730m,
            AppliedRate = 73m,
            RoundingAdjustment = -0.50m,
            FinalPaidAmountBsS = 729.50m
        };
        sale.Items.Add(new SaleItem
        {
            ProductName = "Producto A",
            Quantity = 2m,
            UnitPrice = 5m,
            Subtotal = 10m,
            UnitPriceBsS = 365m,
            SubtotalBsS = 730m
        });
        sale.Payments.Add(new SalePayment
        {
            PaymentMethod = new PaymentMethod { Name = "Efectivo", IsCash = true },
            Amount = 10m,
            AmountBsS = 730m,
            ExchangeRate = 73m
        });

        var context = SaleReceiptContext.CreateFrom(sale);

        Assert.Equal(90210, context.InvoiceNumber);
        Assert.Equal(10m, context.TotalUSD);
        Assert.Equal(730m, context.TotalBsS);
        Assert.Equal(73m, context.AppliedRate);
        Assert.Equal(-0.50m, context.RoundingAdjustment);
        Assert.Equal(729.50m, context.FinalPaidAmountBsS);
        var line = Assert.Single(context.Lines);
        Assert.Equal("Producto A", line.ProductName);
        var payment = Assert.Single(context.Payments);
        Assert.Equal("Efectivo", payment.MethodName);
    }

    [Fact]
    public void NullReceiptPrintQueue_Enqueue_DoesNotThrow()
    {
        var queue = new NullReceiptPrintQueue();
        var context = new SaleReceiptContext { SaleId = 1 };

        var ex = Record.Exception(() => queue.Enqueue(context));

        Assert.Null(ex);
    }

    [Fact]
    public void StubRendererAndQueue_ImplementTheirRespectiveContracts()
    {
        Assert.IsAssignableFrom<IReceiptDocumentRenderer>(new StubReceiptRenderer());
        Assert.IsAssignableFrom<IReceiptPrintQueue>(new NullReceiptPrintQueue());
    }
}