using System;
using System.Text;
using Sales.Module.Entities;
using Sales.Module.Receipts;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class SaleReceiptRendererTests
{
    private static SaleReceiptContext BuildContext()
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
            SubtotalBsS = 730m,
            AppliedRate = 73m,
            RoundingAdjustment = 0m,
            FinalPaidAmountBsS = 730m
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
        return SaleReceiptContext.CreateFrom(sale);
    }

    [Fact]
    public void SaleReceiptRenderer_Render_ProducesPdfBytesWithFilename()
    {
        var renderer = new SaleReceiptRenderer();
        var context = BuildContext();

        var document = renderer.Render(context);

        Assert.NotNull(document.Bytes);
        Assert.NotEmpty(document.Bytes);
        Assert.Equal("application/pdf", document.ContentType);
        Assert.Contains("Recibo_90210_7_", document.FileName);
        var header = Encoding.ASCII.GetString(document.Bytes.AsSpan(0, 5).ToArray());
        Assert.Equal("%PDF-", header);
    }

    [Fact]
    public void SaleReceiptRenderer_Render_IncludesSnapshotAmounts()
    {
        var renderer = new SaleReceiptRenderer();
        var context = BuildContext();

        var document = renderer.Render(context);
        var content = Encoding.ASCII.GetString(document.Bytes!);

        Assert.Contains("90210", content);
        Assert.Contains("730.00", content);
        Assert.Contains("73.0000", content);
        Assert.Contains("RECIBO NO FISCAL", content);
        Assert.Contains("Producto A", content);
    }
}