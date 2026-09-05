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

    [Fact]
    public async Task GetExpectedTotalsByPaymentMethodAsync_LateEveningVenezuelaTime_IncludesEveningSales()
    {
        var (service, context) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        // Fecha de prueba: 2026-09-04 a las 21:00 VET (UTC-4), lo cual equivale a 2026-09-05 01:00 UTC
        var tz = Core.Helpers.TimeZoneHelper.GetVenezuelaTimeZone();
        var venEveningTime = new DateTime(2026, 9, 4, 21, 0, 0, DateTimeKind.Unspecified);
        var utcEveningTime = TimeZoneInfo.ConvertTimeToUtc(venEveningTime, tz); // 2026-09-05 01:00:00 UTC

        // Venta completada a las 21:00 hora de Venezuela
        var sale = new Sale { Id = 888, Status = SaleStatus.Completed, Date = utcEveningTime };
        context.Sales.Add(sale);
        context.SalePayments.Add(new SalePayment { SaleId = 888, PaymentMethodId = 1, AmountBsS = 1200m });
        await context.SaveChangesAsync();

        // Consulta de esperados a las 22:30 hora de Venezuela (02:30 UTC del día siguiente)
        var utcQueryTime = utcEveningTime.AddMinutes(90);
        var totals = await service.GetExpectedTotalsByPaymentMethodAsync(utcQueryTime);

        var efectivoUsdTotal = totals.First(t => t.PaymentMethodId == 1);
        Assert.Equal(1200m, efectivoUsdTotal.ExpectedAmountBsS);
    }

    [Fact]
    public async Task CreateClosureAsync_MissingActivePaymentMethods_PopulatesAuthoritativeTotals()
    {
        var (service, context) = CreateService();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        // Venta con Pago Móvil (id 4)
        var sale = new Sale { Id = 999, Status = SaleStatus.Completed, Date = DateTime.UtcNow };
        context.Sales.Add(sale);
        context.SalePayments.Add(new SalePayment { SaleId = 999, PaymentMethodId = 4, AmountBsS = 750m });
        await context.SaveChangesAsync();

        // El cliente envía solo el método 1 y omite los demás métodos activos (incluyendo Pago Móvil)
        var closure = new DailyClosure
        {
            ClosureDate = DateTime.UtcNow,
            UserId = "Cajero1",
            Details = new List<ClosureDetail>
            {
                new ClosureDetail { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ActualAmountBsS = 500m, ExpectedAmountBsS = 0m }
            }
        };

        var saved = await service.CreateClosureAsync(closure);

        // El método 4 omitido debe haberse poblado automáticamente con su total esperado de 750 Bs.S
        var method4Detail = saved.Details.FirstOrDefault(d => d.PaymentMethodId == 4);
        Assert.NotNull(method4Detail);
        Assert.Equal(750m, method4Detail.ExpectedAmountBsS);
        // Para métodos no-efectivo se consolida automáticamente
        Assert.Equal(750m, method4Detail.ActualAmountBsS);
        Assert.Equal(0m, method4Detail.DifferenceBsS);
    }
}
