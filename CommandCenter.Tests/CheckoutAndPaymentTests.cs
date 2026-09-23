using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Desktop.Client.ViewModels;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using CommandCenter.Tests.TestHelpers;
using Sales.Module.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace CommandCenter.Tests;

public class CheckoutAndPaymentTests
{
    private const int TestActorId = 42;

    private SalesDbContext GetInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    private (SalesService service, SalesDbContext context) CreateSalesService(SalesDbContext context)
    {
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockCashDrawer
            .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);
        return (service, context);
    }

    [Fact]
    public async Task Checkout_WithMixedPaymentsAndUnusedZeroMethods_SavesSuccessfully()
    {
        using var context = GetInMemoryDbContext();
        var (service, _) = CreateSalesService(context);

        var sale = new Sale
        {
            Id = 1,
            TotalUSD = 100m,
            Subtotal = 100m,
            AppliedRate = 50m,
            TotalBsS = 5000m,
            SubtotalBsS = 5000m,
            Status = SaleStatus.Pending
        };
        context.Sales.Add(sale);

        context.PaymentMethods.AddRange(
            new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true },
            new PaymentMethod { Id = 2, Name = "Punto de Venta", IsCash = false },
            new PaymentMethod { Id = 3, Name = "Pago Móvil", IsCash = false },
            new PaymentMethod { Id = 4, Name = "Zelle", IsCash = false }
        );
        await context.SaveChangesAsync();

        var payments = new List<PaymentInfo>
        {
            new PaymentInfo(1, 50m, 2500m, null),
            new PaymentInfo(2, 50m, 2500m, null),
            new PaymentInfo(3, 0m, 0m, null),
            new PaymentInfo(4, 0m, 0m, null)
        };

        int invoiceNum = await service.CompleteSaleAsync(sale.Id, 50m, payments);

        var savedSale = await context.Sales.Include(s => s.Payments).FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, savedSale.Status);
        Assert.Equal(2, savedSale.Payments.Count);
        Assert.All(savedSale.Payments, p => Assert.True(p.Amount > 0));
    }

    [Fact]
    public async Task Checkout_PaymentInBsS_ConvertsToUsdCorrectly()
    {
        using var context = GetInMemoryDbContext();
        var (service, _) = CreateSalesService(context);

        var sale = new Sale
        {
            Id = 2,
            TotalUSD = 50m,
            Subtotal = 50m,
            AppliedRate = 50m,
            TotalBsS = 2500m,
            SubtotalBsS = 2500m,
            Status = SaleStatus.Pending
        };
        context.Sales.Add(sale);
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true });
        await context.SaveChangesAsync();

        var payments = new List<PaymentInfo>
        {
            new PaymentInfo(1, 0m, 2500m, null)
        };

        await service.CompleteSaleAsync(sale.Id, 50m, payments);

        var savedSale = await context.Sales.Include(s => s.Payments).FirstAsync(s => s.Id == sale.Id);
        Assert.Single(savedSale.Payments);
        Assert.Equal(50m, savedSale.Payments[0].Amount);
        Assert.Equal(2500m, savedSale.Payments[0].AmountBsS);
    }

    [Fact]
    public async Task Checkout_WithZeroAppliedRate_IsRejectedOrHandled()
    {
        using var context = GetInMemoryDbContext();
        var (service, _) = CreateSalesService(context);

        var sale = new Sale
        {
            Id = 3,
            TotalUSD = 100m,
            Status = SaleStatus.Pending
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var payments = new List<PaymentInfo>
        {
            new PaymentInfo(1, 100m, 0m, null)
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteSaleAsync(sale.Id, 0m, payments));
        Assert.Contains("Tasa de cambio AppliedRate inválida", ex.Message);
    }

    [Fact]
    public async Task Checkout_WithCashPaymentContainingCents_ThrowsValidationError()
    {
        using var context = GetInMemoryDbContext();
        var (service, _) = CreateSalesService(context);

        var sale = new Sale
        {
            Id = 20,
            TotalUSD = 100m,
            Subtotal = 100m,
            AppliedRate = 50m,
            TotalBsS = 5000m,
            SubtotalBsS = 5000m,
            Status = SaleStatus.Pending
        };
        context.Sales.Add(sale);

        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Efectivo", IsCash = true });
        await context.SaveChangesAsync();

        // Efectivo con centavos (2500.50 Bs.S) debe ser rechazado por la API
        var payments = new List<PaymentInfo>
        {
            new PaymentInfo(1, 50m, 2500.50m, null)
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.CompleteSaleAsync(sale.Id, 50m, payments));
        Assert.Contains("solo acepta montos enteros", ex.Message);
    }

    [Fact]
    public async Task Checkout_WithCashPaymentIntegerAmount_SavesSuccessfully()
    {
        using var context = GetInMemoryDbContext();
        var (service, _) = CreateSalesService(context);

        var sale = new Sale
        {
            Id = 21,
            TotalUSD = 100m,
            Subtotal = 100m,
            AppliedRate = 50m,
            TotalBsS = 5000m,
            SubtotalBsS = 5000m,
            Status = SaleStatus.Pending
        };
        context.Sales.Add(sale);

        context.PaymentMethods.AddRange(
            new PaymentMethod { Id = 1, Name = "Efectivo", IsCash = true },
            new PaymentMethod { Id = 2, Name = "Punto de Venta", IsCash = false }
        );
        await context.SaveChangesAsync();

        // Efectivo con monto entero (2500 Bs.S) + electrónico con decimales (2500.50 Bs.S) deben aceptarse
        var payments = new List<PaymentInfo>
        {
            new PaymentInfo(1, 50m, 2500m, null),
            new PaymentInfo(2, 50.01m, 2500.50m, null)
        };

        int invoiceNum = await service.CompleteSaleAsync(sale.Id, 50m, payments);

        var savedSale = await context.Sales.Include(s => s.Payments).FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, savedSale.Status);
        Assert.Equal(2, savedSale.Payments.Count);
        Assert.True(invoiceNum > 0);
    }

    [Fact]
    public async Task AddPaymentToHoldSale_WithCashPaymentContainingCents_ThrowsValidationError()
    {
        using var context = GetInMemoryDbContext();
        var (service, _) = CreateSalesService(context);

        var sale = new Sale
        {
            Id = 22,
            TotalUSD = 100m,
            Subtotal = 100m,
            AppliedRate = 50m,
            TotalBsS = 5000m,
            SubtotalBsS = 5000m,
            Status = SaleStatus.OnHold
        };
        sale.ClaimedByUserId = TestActorId;
        sale.ClaimAction = SaleClaimAction.Editing;
        sale.ClaimedByUserName = "Test Actor";
        sale.ClaimedAtUtc = DateTime.UtcNow;
        context.Sales.Add(sale);
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Efectivo", IsCash = true });
        await context.SaveChangesAsync();

        var request = new AddPaymentRequestDto
        {
            PaymentMethodId = 1,
            AmountBsS = 10.50m,
            AmountUSD = 0.21m,
            ExchangeRate = 50m
        };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.AddPaymentToHoldSaleAsync(sale.Id, request, actingUserId: TestActorId));
        Assert.Contains("solo acepta montos enteros", ex.Message);
    }

    [Fact]
    public void Checkout_WithInvariantCultureDecimalParsing_ParsesCorrectly()
    {
        Assert.Equal(150.50m, CheckoutViewModel.ParseAmount("150.50"));
        Assert.Equal(150.50m, CheckoutViewModel.ParseAmount("150,50"));
        Assert.Equal(1500.00m, CheckoutViewModel.ParseAmount("1,500.00"));
        Assert.Equal(1500m, CheckoutViewModel.ParseAmount("1500"));
        Assert.Equal(0m, CheckoutViewModel.ParseAmount(""));
    }

    [Fact]
    public async Task Checkout_WithTotalZeroSum_IsRejectedByValidation()
    {
        using var context = GetInMemoryDbContext();
        var (service, _) = CreateSalesService(context);

        var sale = new Sale
        {
            Id = 4,
            TotalUSD = 100m,
            Subtotal = 100m,
            AppliedRate = 50m,
            TotalBsS = 5000m,
            SubtotalBsS = 5000m,
            Status = SaleStatus.Pending
        };
        context.Sales.Add(sale);
        context.PaymentMethods.AddRange(
            new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true },
            new PaymentMethod { Id = 2, Name = "Punto de Venta", IsCash = false }
        );
        await context.SaveChangesAsync();

        var payments = new List<PaymentInfo>
        {
            new PaymentInfo(1, 0m, 0m, null),
            new PaymentInfo(2, 0m, 0m, null)
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.CompleteSaleAsync(sale.Id, 50m, payments));
        Assert.Contains("Rechazo Defensivo: El total acumulado de los métodos de pago es <= 0", ex.Message);
    }

    [Fact]
    public async Task GetExpectedTotals_ReturnsAllActivePaymentMethods_EvenWhenNoSalesExistToday()
    {
        using var context = GetInMemoryDbContext();
        context.PaymentMethods.AddRange(
            new PaymentMethod { Id = 1, Name = "Efectivo USD", IsActive = true, DisplayOrder = 1 },
            new PaymentMethod { Id = 2, Name = "Punto de Venta", IsActive = true, DisplayOrder = 2 },
            new PaymentMethod { Id = 3, Name = "Inactivo Method", IsActive = false, DisplayOrder = 3 }
        );
        await context.SaveChangesAsync();

        var service = DailyClosureTestHelper.CreateService(context);
        var totals = await service.GetExpectedTotalsByPaymentMethodAsync(DateTime.UtcNow);

        Assert.Equal(2, totals.Count);
        Assert.Contains(totals, t => t.PaymentMethodId == 1 && t.ExpectedAmountBsS == 0m);
        Assert.Contains(totals, t => t.PaymentMethodId == 2 && t.ExpectedAmountBsS == 0m);
        Assert.DoesNotContain(totals, t => t.PaymentMethodId == 3);
    }

    [Fact]
    public async Task CreateClosureFromCommand_AutoCompletesMissingActiveMethods_WithZeroAmount()
    {
        using var context = GetInMemoryDbContext();
        context.PaymentMethods.AddRange(
            new PaymentMethod { Id = 1, Name = "Efectivo USD", IsActive = true, DisplayOrder = 1 },
            new PaymentMethod { Id = 2, Name = "Punto de Venta", IsActive = true, DisplayOrder = 2 }
        );
        await context.SaveChangesAsync();

        var service = DailyClosureTestHelper.CreateService(context);
        var command = new CreateClosureCommand(
            DateTime.UtcNow,
            "Cashier1",
            null,
            new List<DeclaredPaymentAmount>
            {
                new(1, 100m)
            });

        var result = await service.CreateClosureFromCommandAsync(command, CancellationToken.None);

        var details = await context.ClosureDetails
            .AsNoTracking()
            .Where(d => d.DailyClosureId == result.ClosureId)
            .ToListAsync();

        Assert.Equal(2, details.Count);
        var missingMethod = details.FirstOrDefault(d => d.PaymentMethodId == 2);
        Assert.NotNull(missingMethod);
        Assert.Equal(0m, missingMethod!.ActualAmountBsS);
        Assert.Equal(0m, missingMethod.ExpectedAmountBsS);
    }

    [Fact]
    public async Task GetExpectedTotals_ExcludesSalesPriorToLastClosure_ResetsToZeroAfterClosure()
    {
        using var context = GetInMemoryDbContext();
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Efectivo USD", IsActive = true });
        
        var baseTime = DateTime.UtcNow.Date.AddHours(10); // 10:00 AM today
        var sale = new Sale { Id = 10, Status = SaleStatus.Completed, Date = baseTime };
        context.Sales.Add(sale);
        context.SalePayments.Add(new SalePayment { SaleId = 10, PaymentMethodId = 1, AmountBsS = 500m });
        await context.SaveChangesAsync();

        var service = DailyClosureTestHelper.CreateService(context);

        // Before closure: expected amount should be 500.00 Bs.S
        var totalsBefore = await service.GetExpectedTotalsByPaymentMethodAsync(baseTime);
        Assert.Equal(500m, totalsBefore.First(t => t.PaymentMethodId == 1).ExpectedAmountBsS);

        // Perform closure at 12:00 PM today
        var command = new CreateClosureCommand(
            baseTime.AddHours(2),
            "Admin",
            null,
            new List<DeclaredPaymentAmount>
            {
                new(1, 10m)
            });
        await service.CreateClosureFromCommandAsync(command, CancellationToken.None);

        // Immediately after closure (12:01 PM today): expected totals should reset to 0.00 Bs.S
        var totalsAfter = await service.GetExpectedTotalsByPaymentMethodAsync(baseTime.AddHours(2).AddMinutes(1));
        Assert.Equal(0m, totalsAfter.First(t => t.PaymentMethodId == 1).ExpectedAmountBsS);
    }

    [Fact]
    public async Task GetExpectedTotals_IncludesSalesAfterLastClosure_AccumulatesOnlyNewSales()
    {
        using var context = GetInMemoryDbContext();
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Efectivo USD", IsActive = true });

        var baseTime = DateTime.UtcNow.Date.AddHours(10); // 10:00 AM today
        var saleOld = new Sale { Id = 11, Status = SaleStatus.Completed, Date = baseTime };
        context.Sales.Add(saleOld);
        context.SalePayments.Add(new SalePayment { SaleId = 11, PaymentMethodId = 1, AmountBsS = 300m });
        await context.SaveChangesAsync();

        var service = DailyClosureTestHelper.CreateService(context);

        // Perform closure at 12:00 PM
        var command = new CreateClosureCommand(
            baseTime.AddHours(2),
            "Admin",
            null,
            new List<DeclaredPaymentAmount>
            {
                new(1, 6m)
            });
        await service.CreateClosureFromCommandAsync(command, CancellationToken.None);

        // Register new sale at 14:00 PM (after closure)
        var saleNew = new Sale { Id = 12, Status = SaleStatus.Completed, Date = baseTime.AddHours(4) };
        context.Sales.Add(saleNew);
        context.SalePayments.Add(new SalePayment { SaleId = 12, PaymentMethodId = 1, AmountBsS = 200m });
        await context.SaveChangesAsync();

        // Query expected totals at 15:00 PM
        var totalsNewShift = await service.GetExpectedTotalsByPaymentMethodAsync(baseTime.AddHours(5));

        // Expected amount must be ONLY 200.00 Bs.S (excluding the 300.00 Bs.S from before the closure)
        Assert.Equal(200m, totalsNewShift.First(t => t.PaymentMethodId == 1).ExpectedAmountBsS);
    }

    [Fact]
    public async Task DailyClosure_GeneratesAndSavesReceiptsAutomatically()
    {
        using var context = GetInMemoryDbContext();
        context.PaymentMethods.Add(new PaymentMethod { Id = 1, Name = "Efectivo", IsActive = true });
        await context.SaveChangesAsync();

        var service = DailyClosureTestHelper.CreateService(context);
        var command = new CreateClosureCommand(
            DateTime.UtcNow,
            "Admin",
            "Test closure",
            new List<DeclaredPaymentAmount>
            {
                new(1, 100m)
            });

        var result = await service.CreateClosureFromCommandAsync(command, CancellationToken.None);

        Assert.True(result.ClosureId > 0);
    }

    [Fact]
    public async Task DailyClosure_DoesNotFailIfSavingReceiptFails()
    {
        using var context = GetInMemoryDbContext();
        var service = DailyClosureTestHelper.CreateService(context);
        var command = new CreateClosureCommand(
            DateTime.UtcNow,
            "Admin",
            null,
            new List<DeclaredPaymentAmount>());

        var result = await service.CreateClosureFromCommandAsync(command, CancellationToken.None);

        Assert.NotNull(result);
    }

    [Fact]
    public void GenerateReceiptContent_WithCashier_OmitsExpectedAmounts()
    {
        var closure = new DailyClosure
        {
            ClosureDate = DateTime.UtcNow,
            UserId = "Cajero Turno 1",
            Observation = "Cierre a ciegas",
            TotalActualBsS = 250m,
            Details = new List<ClosureDetail>
            {
                new ClosureDetail { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ActualAmountBsS = 250m, ExpectedAmountBsS = 300m, DifferenceBsS = -50m }
            }
        };

        string receipt = DailyClosureService.GenerateReceiptContent(ShiftReportMapper.MapClosure(closure), isBlind: true);

        Assert.Contains("COMPROBANTE DE ARQUEO A CIEGAS", receipt);
        Assert.Contains("MÉTODO DE PAGO", receipt);
        Assert.Contains("MONEDA", receipt);
        Assert.Contains("MONTO DECLARADO (Bs.S)", receipt);
        Assert.Contains("Efectivo USD", receipt);
        Assert.Contains("250,00", receipt);
        Assert.Contains("TOTALES", receipt);
        Assert.DoesNotContain("MONTO SISTEMA", receipt);
        Assert.DoesNotContain("DIFERENCIA", receipt);
        Assert.DoesNotContain("DIFERENCIA TOTAL", receipt);
        Assert.DoesNotContain("ESTADO DE CAJA", receipt);
    }

    [Fact]
    public void GenerateReceiptContent_WithAdmin_IncludesFullAudit()
    {
        var closure = new DailyClosure
        {
            ClosureDate = DateTime.UtcNow,
            UserId = "Admin",
            Observation = "Audit OK",
            TotalActualBsS = 500m,
            TotalExpectedBsS = 500m,
            TotalDifferenceBsS = 0m,
            Details = new List<ClosureDetail>
            {
                new ClosureDetail { PaymentMethodId = 1, PaymentMethodName = "Punto de Venta", ActualAmountBsS = 500m, ExpectedAmountBsS = 500m, DifferenceBsS = 0m }
            }
        };

        string receipt = DailyClosureService.GenerateReceiptContent(ShiftReportMapper.MapClosure(closure), isBlind: false);

        Assert.Contains("COMPROBANTE DE CIERRE Y AUDITORÍA DE CAJA", receipt);
        Assert.Contains("MÉTODO DE PAGO", receipt);
        Assert.Contains("MONEDA", receipt);
        Assert.Contains("MONTO DECLARADO (Bs.S)", receipt);
        Assert.Contains("MONTO SISTEMA (Bs.S)", receipt);
        Assert.Contains("DIFERENCIA (Bs.S)", receipt);
        Assert.Contains("TOTALES", receipt);
        Assert.Contains("TOTAL ESPERADO", receipt);
        Assert.Contains("DIFERENCIA TOTAL", receipt);
        Assert.Contains("ESTADO DE CAJA:   Cuadrado", receipt);
    }

    [Fact]
    public void GeneratePdf_GeneratesValidPdfBytesMatchingWebFormat()
    {
        var closure = new DailyClosure
        {
            Id = 42,
            ClosureDate = DateTime.UtcNow,
            UserId = "Admin Test",
            Observation = "V-12345678",
            TotalActualBsS = 1500m,
            TotalExpectedBsS = 1500m,
            TotalDifferenceBsS = 0m,
            Details = new List<ClosureDetail>
            {
                new ClosureDetail { PaymentMethodId = 1, PaymentMethodName = "Efectivo USD", ActualAmountBsS = 1000m, ExpectedAmountBsS = 1000m, DifferenceBsS = 0m },
                new ClosureDetail { PaymentMethodId = 2, PaymentMethodName = "Punto de Venta", ActualAmountBsS = 500m, ExpectedAmountBsS = 500m, DifferenceBsS = 0m }
            }
        };

        byte[] pdfBytes = ClosurePdfGenerator.GeneratePdf(ShiftReportMapper.MapClosure(closure), isBlind: false);

        Assert.NotNull(pdfBytes);
        Assert.True(pdfBytes.Length > 200);

        string header = System.Text.Encoding.ASCII.GetString(pdfBytes, 0, 8);
        Assert.StartsWith("%PDF-", header);
    }
}
