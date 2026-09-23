using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using CommandCenter.Tests.Integration;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;
using SalesService = Sales.Module.Services.SalesService;
using ICashDrawerService = Sales.Module.Interfaces.ICashDrawerService;

namespace CommandCenter.Tests.Unit;

[Collection(PostgresRealCollection.Name)]
public class SalesServicePostgresHistoryTests
{
    [Fact]
    [Trait("Category", "RequiresDocker")]
    public async Task GetSalesHistoryAsync_AgainstRealPostgreSql_ReturnsSeededInvoices()
    {
        // Reescrito en 8.11: version autónoma. NO depende de datos de la BD de desarrollo
        // (antes asumía facturas 216-220 del 04-sep-2026 del CommandCenterDb local, lo que
        // hacía el test irrompible en CI). Ahora siembra sus propias ventas en la ventana
        // calendar del día (Venezuela UTC-4) y valida el filtro real de Postgres.
        var connStr = Environment.GetEnvironmentVariable("TEST_POSTGRES_CONNECTION");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
            {
                throw new InvalidOperationException(
                    "TEST_POSTGRES_CONNECTION no está definida en CI. Configure PostgreSQL real para ejecutar GetSalesHistoryAsync_AgainstRealPostgreSql_ReturnsSeededInvoices.");
            }

            return;
        }

        // Ventana calendario 2026-09-04 en Venezuela (UTC-4): [2026-09-04T04:00Z, 2026-09-05T04:00Z).
        var dayStartUtc = new DateTime(2026, 9, 4, 4, 0, 0, DateTimeKind.Utc);

        using (var seedContext = TestDatabaseFactory.CreatePostgreSqlSalesDbContext())
        {
            Assert.NotNull(seedContext);

            await TestDatabaseFactory.SeedStandardSalesDataAsync(seedContext!);

            var previous = await seedContext!.Sales
                .Where(s => s.InvoiceNumber != null && s.InvoiceNumber.Value >= 216 && s.InvoiceNumber.Value <= 220)
                .ToListAsync();
            seedContext.Sales.RemoveRange(previous);
            await seedContext.SaveChangesAsync();

            var pm = await seedContext.PaymentMethods.SingleAsync(p => p.Name == "Punto de Venta");

            seedContext.Sales.AddRange(
                new Sales.Module.Entities.Sale
                {
                    InvoiceNumber = 216,
                    Date = new DateTime(2026, 9, 5, 2, 28, 0, DateTimeKind.Utc),
                    Status = Sales.Module.Entities.SaleStatus.Completed,
                    TotalUSD = 0.81m,
                    AppliedRate = 813.74m,
                    TotalBsS = 659.13m,
                    Subtotal = 0.81m,
                    SubtotalBsS = 659.13m,
                    FinalPaidAmountBsS = 659.13m
                },
                new Sales.Module.Entities.Sale
                {
                    InvoiceNumber = 217,
                    Date = new DateTime(2026, 9, 4, 23, 10, 0, DateTimeKind.Utc),
                    Status = Sales.Module.Entities.SaleStatus.Completed,
                    TotalUSD = 10.00m,
                    AppliedRate = 813.74m,
                    TotalBsS = 8137.40m,
                    Subtotal = 10.00m,
                    SubtotalBsS = 8137.40m,
                    FinalPaidAmountBsS = 8137.40m
                },
                new Sales.Module.Entities.Sale
                {
                    InvoiceNumber = 218,
                    Date = new DateTime(2026, 9, 4, 22, 44, 0, DateTimeKind.Utc),
                    Status = Sales.Module.Entities.SaleStatus.Completed,
                    TotalUSD = 0.81m,
                    AppliedRate = 813.74m,
                    TotalBsS = 659.13m,
                    Subtotal = 0.81m,
                    SubtotalBsS = 659.13m,
                    FinalPaidAmountBsS = 659.13m,
                    CustomerName = "Consumidor Final",
                    Items = new List<Sales.Module.Entities.SaleItem>
                    {
                        new()
                        {
                            ProductId = 1,
                            ProductName = "Papas Lays",
                            Quantity = 1,
                            UnitPrice = 0.81m,
                            Subtotal = 0.81m,
                            UnitPriceBsS = 659.13m,
                            SubtotalBsS = 659.13m
                        }
                    },
                    Payments = new List<Sales.Module.Entities.SalePayment>
                    {
                        new()
                        {
                            PaymentMethodId = pm.Id,
                            Amount = 0.81m,
                            AmountBsS = 659.13m,
                            ExchangeRate = 813.74m,
                            ReferenceNumber = "1234"
                        }
                    }
                },
                new Sales.Module.Entities.Sale
                {
                    InvoiceNumber = 219,
                    Date = new DateTime(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc),
                    Status = Sales.Module.Entities.SaleStatus.Completed,
                    TotalUSD = 5.00m,
                    AppliedRate = 813.74m,
                    TotalBsS = 4068.70m,
                    Subtotal = 5.00m,
                    SubtotalBsS = 4068.70m,
                    FinalPaidAmountBsS = 4068.70m
                },
                new Sales.Module.Entities.Sale
                {
                    InvoiceNumber = 220,
                    Date = new DateTime(2026, 9, 4, 6, 30, 0, DateTimeKind.Utc),
                    Status = Sales.Module.Entities.SaleStatus.Completed,
                    TotalUSD = 2.50m,
                    AppliedRate = 813.74m,
                    TotalBsS = 2034.35m,
                    Subtotal = 2.50m,
                    SubtotalBsS = 2034.35m,
                    FinalPaidAmountBsS = 2034.35m
                });

            await seedContext.SaveChangesAsync();
        }

        using var realContext = TestDatabaseFactory.CreatePostgreSqlSalesDbContext()!;

        var inventoryMock = new Mock<IInventoryService>();
        var mediatorMock = new Mock<IMediator>();
        var cashDrawerMock = new Mock<ICashDrawerService>();
        var settingsMock = new Mock<ISystemSettingsService>();

        var realService = new SalesService(realContext, inventoryMock.Object, mediatorMock.Object, cashDrawerMock.Object, settingsMock.Object);

        // Consultar el día calendario 2026-09-04 (Venezuela UTC-4) -> incluye las 5 facturas sembradas.
        var filterDate = new DateTime(2026, 9, 4);
        var (items, totalCount) = await realService.GetSalesHistoryAsync(1, 25, filterDate, filterDate);

        Assert.True(totalCount >= 5, $"Se esperaban al menos 5 ventas sembradas de hoy en PostgreSQL real, pero se obtuvieron {totalCount}.");
        var list = items.ToList();
        Assert.Contains(list, s => s.InvoiceNumber == 216);
        Assert.Contains(list, s => s.InvoiceNumber == 217);
        Assert.Contains(list, s => s.InvoiceNumber == 218);
        Assert.Contains(list, s => s.InvoiceNumber == 219);
        Assert.Contains(list, s => s.InvoiceNumber == 220);

        // Verificar GetSaleHistoryDetailAsync para factura 218 (con item y pago sembrados)
        var inv218 = list.First(s => s.InvoiceNumber == 218);
        var detail218 = await realService.GetSaleHistoryDetailAsync(inv218.Id);
        Assert.NotNull(detail218);
        Assert.NotEmpty(detail218.Items);
        Assert.NotEmpty(detail218.Payments);
        Assert.Equal(813.74m, detail218.AppliedRate);
        Assert.Equal(0.81m, detail218.TotalUSD);
        Assert.Equal(659.13m, detail218.TotalBsS);
        Assert.Equal("Punto de Venta", detail218.Payments[0].MethodName);

        // Probar serialización/deserialización HTTP hacia Desktop.Client.Services.SaleHistoryDto
        var jsonOptions = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var jsonStr = System.Text.Json.JsonSerializer.Serialize(detail218, jsonOptions);
        var clientDto = System.Text.Json.JsonSerializer.Deserialize<Desktop.Client.Services.SaleHistoryDto>(jsonStr, jsonOptions);
        Assert.NotNull(clientDto);
        Assert.NotEmpty(clientDto.Items);
        Assert.NotEmpty(clientDto.Payments);
        Assert.Equal(detail218.AppliedRate, clientDto.AppliedRate);
        Assert.Equal(detail218.TotalUSD, clientDto.TotalUSD);
        Assert.Equal(detail218.TotalBsS, clientDto.TotalBsS);
        Assert.Equal(detail218.InvoiceNumber, clientDto.InvoiceNumber);
    }
}
