using System;
using System.Collections.Generic;
using System.Text;
using Sales.Module.Entities;
using Sales.Module.Receipts;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class DeliveryNotePdfGeneratorTests
{
    [Fact]
    public void BuildPdf_IncludesEventSnapshotLocalTimeAndPendingSummary()
    {
        var delivery = new SaleDelivery
        {
            Id = 5,
            SaleId = 100,
            DeliveredAt = new DateTime(2026, 10, 3, 12, 30, 0, DateTimeKind.Utc),
            DeliveredByName = "Ana Paredes",
            Items = new List<SaleDeliveryItem>
            {
                new()
                {
                    ProductId = 7,
                    ProductName = "Custody Product",
                    QuantityDelivered = 4m
                }
            }
        };
        var sale = new Sale
        {
            Id = 100,
            InvoiceNumber = 1100,
            CustomerName = "Marta Ruiz",
            CustomerCedula = "V-12345678"
        };
        var pendingItems = new List<DeliveryNotePendingItem>
        {
            new("Custody Product", 6m)
        };

        var pdf = DeliveryNotePdfGenerator.BuildPdf(delivery, sale, pendingItems);
        var content = Encoding.ASCII.GetString(pdf);

        Assert.NotEmpty(pdf);
        Assert.Equal("%PDF-", content[..5]);
        Assert.Contains("NOTA DE DESPACHO", content);
        Assert.Contains("NO ES UNA FACTURA FISCAL", content);
        Assert.Contains("Fecha de retiro: 03/10/2026 08:30", content);
        Assert.Contains("Cajero: Ana Paredes", content);
        Assert.Contains("Venta: 100", content);
        Assert.Contains("Nro. Factura: #01100", content);
        Assert.Contains("Cliente: Marta Ruiz", content);
        Assert.Contains("Cedula: V-12345678", content);
        Assert.Contains("Custody Product", content);
        Assert.Contains("(4) Tj", content);
        Assert.Contains("PENDIENTES RESTANTES", content);
        Assert.Contains("(6) Tj", content);
        Assert.Contains("TOTAL PENDIENTE", content);
    }
}
