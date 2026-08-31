using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class DailyClosureServiceUnitTests
{
    private (DailyClosureService service, SalesDbContext context) CreateService()
    {
        var context = TestDatabaseFactory.CreateSalesDbContext();
        var service = new DailyClosureService(context);
        return (service, context);
    }

    [Fact]
    public async Task GetExpectedTotals_ReturnsActivePaymentMethods_EvenWithNoSales()
    {
        var (service, context) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var totals = await service.GetExpectedTotalsByPaymentMethodAsync(DateTime.UtcNow);

        Assert.NotEmpty(totals);
        Assert.All(totals, t => Assert.Equal(0m, t.ExpectedAmountBsS));
    }

    [Fact]
    public async Task CreateClosureAsync_CalculatesTotalDifferencesCorrectly()
    {
        var (service, context) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var closure = new DailyClosure
        {
            ClosureDate = DateTime.UtcNow,
            UserId = "Admin1",
            Observation = "Cierre Normal",
            Details = new List<ClosureDetail>
            {
                new ClosureDetail { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ExpectedAmountBsS = 1000m, ActualAmountBsS = 1050m }, // +50
                new ClosureDetail { PaymentMethodId = 3, PaymentMethodName = "Punto de Venta", ExpectedAmountBsS = 2000m, ActualAmountBsS = 1980m } // -20
            }
        };

        var saved = await service.CreateClosureAsync(closure);

        Assert.NotNull(saved);
        Assert.True(saved.Id > 0);
        Assert.Equal(3000m, saved.TotalExpectedBsS);
        Assert.Equal(3030m, saved.TotalActualBsS);
        Assert.Equal(30m, saved.TotalDifferenceBsS); // Sobrante neto de 30 BsS
    }

    [Fact]
    public async Task CreateClosureAsync_WithNegativeDeclaredAmount_ThrowsArgumentException()
    {
        var (service, context) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var closure = new DailyClosure
        {
            ClosureDate = DateTime.UtcNow,
            UserId = "Admin",
            Details = new List<ClosureDetail>
            {
                new ClosureDetail { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ExpectedAmountBsS = 100m, ActualAmountBsS = -50m }
            }
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.CreateClosureAsync(closure));
        Assert.Contains("no puede ser negativo", ex.Message);
    }

    [Fact]
    public void GenerateReceiptContent_BlindMode_HidesDifferencesAndExpectedAmounts()
    {
        var closure = new DailyClosure
        {
            ClosureDate = DateTime.UtcNow,
            UserId = "Cajero Turno Manana",
            TotalActualBsS = 1500m,
            TotalExpectedBsS = 1600m,
            TotalDifferenceBsS = -100m,
            Details = new List<ClosureDetail>
            {
                new ClosureDetail { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ActualAmountBsS = 1500m, ExpectedAmountBsS = 1600m, DifferenceBsS = -100m }
            }
        };

        string receipt = DailyClosureService.GenerateReceiptContent(closure, isBlind: true);

        Assert.Contains("COMPROBANTE DE ARQUEO A CIEGAS", receipt);
        Assert.Contains("Cajero Turno Manana", receipt);
        Assert.Contains("MONTO DECLARADO", receipt);
        Assert.DoesNotContain("MONTO SISTEMA", receipt);
        Assert.DoesNotContain("DIFERENCIA", receipt);
        Assert.DoesNotContain("ESTADO DE CAJA", receipt);
    }

    [Fact]
    public void GenerateReceiptContent_AdminAuditMode_ShowsFullBreakdownAndDrawerStatus()
    {
        var closure = new DailyClosure
        {
            ClosureDate = DateTime.UtcNow,
            UserId = "Administrador General",
            TotalActualBsS = 2000m,
            TotalExpectedBsS = 2000m,
            TotalDifferenceBsS = 0m,
            Details = new List<ClosureDetail>
            {
                new ClosureDetail { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ActualAmountBsS = 1000m, ExpectedAmountBsS = 1000m, DifferenceBsS = 0m },
                new ClosureDetail { PaymentMethodId = 3, PaymentMethodName = "Punto de Venta", ActualAmountBsS = 1000m, ExpectedAmountBsS = 1000m, DifferenceBsS = 0m }
            }
        };

        string receipt = DailyClosureService.GenerateReceiptContent(closure, isBlind: false);

        Assert.Contains("COMPROBANTE DE CIERRE Y AUDITORÍA DE CAJA", receipt);
        Assert.Contains("Administrador General", receipt);
        Assert.Contains("MONTO SISTEMA (Bs.S)", receipt);
        Assert.Contains("DIFERENCIA (Bs.S)", receipt);
        Assert.Contains("ESTADO DE CAJA:   Cuadrado", receipt);
    }
}
