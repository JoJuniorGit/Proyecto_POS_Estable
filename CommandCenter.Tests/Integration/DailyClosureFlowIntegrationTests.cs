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

namespace CommandCenter.Tests.Integration;

public class DailyClosureFlowIntegrationTests
{
    [Fact]
    public async Task DailyClosureFlow_TracksSales_ReconcilesExpectedBalances_AndResetsAccumulatorsForNextShift()
    {
        // 1. Setup Context & Service
        var context = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);
        var closureService = new DailyClosureService(context);

        var baseTime = DateTime.UtcNow.Date.AddHours(9); // 9:00 AM

        // 2. Register Sales during Shift 1
        // Venta 1: Efectivo USD (1000 Bs.S)
        var sale1 = new Sale { Id = 101, Status = SaleStatus.Completed, Date = baseTime.AddHours(1) };
        context.Sales.Add(sale1);
        context.SalePayments.Add(new SalePayment { SaleId = 101, PaymentMethodId = 1, AmountBsS = 1000m });

        // Venta 2: Punto de Venta (2500 Bs.S)
        var sale2 = new Sale { Id = 102, Status = SaleStatus.Completed, Date = baseTime.AddHours(2) };
        context.Sales.Add(sale2);
        context.SalePayments.Add(new SalePayment { SaleId = 102, PaymentMethodId = 3, AmountBsS = 2500m });

        await context.SaveChangesAsync();

        // 3. Query Expected Totals Before Closure (at 12:00 PM)
        var totalsBefore = await closureService.GetExpectedTotalsByPaymentMethodAsync(baseTime.AddHours(3));
        Assert.Equal(1000m, totalsBefore.First(t => t.PaymentMethodId == 1).ExpectedAmountBsS);
        Assert.Equal(2500m, totalsBefore.First(t => t.PaymentMethodId == 3).ExpectedAmountBsS);
        Assert.Equal(0m, totalsBefore.First(t => t.PaymentMethodId == 4).ExpectedAmountBsS); // Pago móvil sin ventas

        // 4. Perform Shift 1 Daily Closure (at 12:30 PM)
        var closureShift1 = new DailyClosure
        {
            ClosureDate = baseTime.AddHours(3).AddMinutes(30),
            UserId = "Admin Auditor",
            Observation = "Cierre Turno 1 OK",
            Details = new List<ClosureDetail>
            {
                new ClosureDetail { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ExpectedAmountBsS = 1000m, ActualAmountBsS = 1000m },
                new ClosureDetail { PaymentMethodId = 3, PaymentMethodName = "Punto de Venta", ExpectedAmountBsS = 2500m, ActualAmountBsS = 2500m }
            }
        };

        var savedClosure = await closureService.CreateClosureAsync(closureShift1);
        Assert.NotNull(savedClosure);
        Assert.Equal(3500m, savedClosure.TotalExpectedBsS);
        Assert.Equal(3500m, savedClosure.TotalActualBsS);
        Assert.Equal(0m, savedClosure.TotalDifferenceBsS);

        // 5. Query Expected Totals Immediately After Closure (at 12:35 PM)
        var totalsImmediatelyAfter = await closureService.GetExpectedTotalsByPaymentMethodAsync(baseTime.AddHours(3).AddMinutes(35));
        Assert.All(totalsImmediatelyAfter, t => Assert.Equal(0m, t.ExpectedAmountBsS));

        // 6. Register New Sale in Shift 2 (at 14:00 PM)
        var saleShift2 = new Sale { Id = 103, Status = SaleStatus.Completed, Date = baseTime.AddHours(5) };
        context.Sales.Add(saleShift2);
        context.SalePayments.Add(new SalePayment { SaleId = 103, PaymentMethodId = 1, AmountBsS = 800m });
        await context.SaveChangesAsync();

        // 7. Query Expected Totals for Shift 2 (at 17:00 PM)
        var totalsShift2 = await closureService.GetExpectedTotalsByPaymentMethodAsync(baseTime.AddHours(8));
        Assert.Equal(800m, totalsShift2.First(t => t.PaymentMethodId == 1).ExpectedAmountBsS);
        Assert.Equal(0m, totalsShift2.First(t => t.PaymentMethodId == 3).ExpectedAmountBsS);
    }
}
