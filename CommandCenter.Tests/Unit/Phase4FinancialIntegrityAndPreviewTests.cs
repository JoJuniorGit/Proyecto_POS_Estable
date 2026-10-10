using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class Phase4FinancialIntegrityAndPreviewTests
{
    [Fact]
    public void PricingCalculator_ToUSD_SupportsConfigurablePrecision()
    {
        decimal amountBsS = 10m;
        decimal exchangeRate = 36.42m;

        // Default 2 decimals
        decimal defaultResult = PricingCalculator.ToUSD(amountBsS, exchangeRate);
        Assert.Equal(0.27m, defaultResult);

        // 4 decimals high precision [8C-M1]
        decimal highPrecisionResult = PricingCalculator.ToUSD(amountBsS, exchangeRate, decimals: 4);
        Assert.Equal(0.2746m, highPrecisionResult);
    }

    [Fact]
    public async Task SalesController_GetCheckoutPreview_ComputesAuthoritativeTotalsAndChange()
    {
        // Arrange
        var mockSalesService = new Mock<ISalesService>();
        var mockCurrentUserService = new Mock<Core.Interfaces.ICurrentUserService>();

        var saleDto = new SaleDto
        {
            Id = 42,
            TotalUSD = 15.00m,
            AppliedRate = 50.00m,
            Items = new List<SaleItemDto>()
        };

        mockSalesService.Setup(s => s.GetSaleAsync(42)).ReturnsAsync(saleDto);

        // 8.152 (SEC-08): el preview ancla la tasa vía ISalesService.ResolveCheckoutRateAsync;
        // passthrough explícito porque Moq devolvería 0m (guard 400) sin este setup.
        mockSalesService
            .Setup(s => s.ResolveCheckoutRateAsync(42, It.IsAny<decimal>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync((int _, decimal clientRate, System.Threading.CancellationToken _) => clientRate);

        var controller = new SalesController(
            mockSalesService.Object,
            mockCurrentUserService.Object,
            null,
            null,
            new CheckoutCalculationService(mockSalesService.Object));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
        };
        // Usuario sin rol elevado ni UserId resuelto: IsAuthorizedForSaleAsync permanece tolerante,
        // el chequeo de ownership real lo cubre la capa de autenticación (8.6-B1).

        var previewRequest = new CheckoutPreviewRequest
        {
            ExchangeRate = 50.00m,
            Payments = new List<SalePaymentDto>
            {
                new SalePaymentDto
                {
                    PaymentMethodId = 1, // USD Cash
                    Amount = 20.00m,
                    AmountLocal = 1000.00m
                }
            }
        };

        // Act
        var actionResult = await controller.GetCheckoutPreviewAsync(42, previewRequest);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var preview = Assert.IsType<CheckoutPreviewResponse>(okResult.Value);

        Assert.Equal(15.00m, preview.TotalUSD);
        Assert.Equal(750.00m, preview.TotalBsS);
        Assert.Equal(20.00m, preview.TotalPaidUSD);
        Assert.Equal(1000.00m, preview.TotalPaidBsS);
        Assert.Equal(0m, preview.RemainingBalanceUSD);
        Assert.Equal(0m, preview.RemainingBalanceBsS);
        Assert.True(preview.IsFullyPaid);
        Assert.Equal(5.00m, preview.ChangeDueUSD); // Vuelto de $5 USD
        Assert.Equal(250.00m, preview.ChangeDueBsS); // Vuelto de Bs. 250
    }

    private static SalesDbContext CreateInMemoryContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(x => x.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    private static SalesService CreateSalesService(SalesDbContext context)
    {
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockCashDrawer
            .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        return new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);
    }

    // 8.149 (SEC-01): el ajuste del cliente NUNCA se persiste; el backend lo recalcula contra los
    // pagos persistidos al completar (misma fórmula del checkout-preview). El valor extremo 9999
    // también cubre la remoción del guard defensivo ±1000: el parámetro se ignora, no se valida.
    [Theory]
    [InlineData(250)]
    [InlineData(9999)]
    [InlineData(-500)]
    public async Task CompleteSale_DiscardsClientRoundingAdjustment_AndPersistsServerRecomputation(decimal injectedAdjustment)
    {
        // Arrange: pagos mixtos con residual real conocido en Bs.S = 0.50 (2500.00 + 2500.50 - 5000.00)
        using var context = CreateInMemoryContext();
        var service = CreateSalesService(context);

        var sale = new Sale
        {
            Id = 101,
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
            new PaymentMethod { Id = 2, Name = "Punto de Venta", IsCash = false });
        await context.SaveChangesAsync();

        var payments = new List<PaymentInfo>
        {
            new PaymentInfo(1, 50m, 2500m, null),
            new PaymentInfo(2, 50m, 2500.50m, null)
        };

        // Act
        await service.CompleteSaleAsync(sale.Id, 50m, payments, roundingAdjustment: injectedAdjustment);

        // Assert
        var savedSale = await context.Sales.AsNoTracking().FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, savedSale.Status);
        Assert.Equal(0.50m, savedSale.RoundingAdjustment);
        Assert.NotEqual(injectedAdjustment, savedSale.RoundingAdjustment);
    }

    // 8.149 (SEC-01): con saldo restante > 0.01 USD el ajuste server-authoritative es 0,
    // aunque el cliente inyecte un valor distinto (apartado con abono previo).
    [Fact]
    public async Task CompleteSale_WithPartialPendingPickupBalance_PersistsZeroRoundingAdjustment()
    {
        // Arrange: apartado pagado parcialmente; saldo restante 0.03 USD (dentro de la tolerancia de cierre)
        using var context = CreateInMemoryContext();
        var service = CreateSalesService(context);

        context.Customers.Add(new Customer
        {
            Id = 7,
            Name = "María Pérez",
            CedulaOrRif = "V-12345678",
            Phone = "0414-1234567",
            IsDefault = false
        });
        context.PaymentMethods.Add(new PaymentMethod { Id = 2, Name = "Punto de Venta", IsCash = false });

        var sale = new Sale
        {
            Id = 102,
            CustomerId = 7,
            CustomerName = "María Pérez",
            TotalUSD = 100m,
            Subtotal = 100m,
            AppliedRate = 50m,
            TotalBsS = 5000m,
            SubtotalBsS = 5000m,
            Status = SaleStatus.Pending,
            Payments = new List<SalePayment>
            {
                new SalePayment { PaymentMethodId = 2, Amount = 99.97m, AmountBsS = 4998.50m, ExchangeRate = 50m }
            }
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        // Act
        await service.CompleteSaleAsync(
            sale.Id,
            50m,
            Enumerable.Empty<PaymentInfo>(),
            roundingAdjustment: 250m,
            cashierId: 1,
            isPendingPickup: true);

        // Assert
        var savedSale = await context.Sales.AsNoTracking().FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, savedSale.Status);
        Assert.Equal(SaleDeliveryStatus.PendingPickup, savedSale.DeliveryStatus);
        Assert.Equal(0m, savedSale.RoundingAdjustment);
    }

    // 8.149 (SEC-01): el ajuste persistido al completar debe ser idéntico al del checkout-preview
    // para el mismo conjunto de pagos mixtos (fuente única de la regla de redondeo).
    [Fact]
    public async Task CompleteSale_RoundingAdjustment_MatchesCheckoutPreview_ForSameMixedPayments()
    {
        // Arrange: preview por el endpoint real
        var mockSalesService = new Mock<ISalesService>();
        var mockCurrentUserService = new Mock<ICurrentUserService>();

        mockSalesService.Setup(s => s.GetSaleAsync(42)).ReturnsAsync(new SaleDto
        {
            Id = 42,
            TotalUSD = 100m,
            AppliedRate = 50m,
            Items = new List<SaleItemDto>()
        });

        // 8.152 (SEC-08): passthrough del resolver anclado para conservar la semántica previa.
        mockSalesService
            .Setup(s => s.ResolveCheckoutRateAsync(42, It.IsAny<decimal>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync((int _, decimal clientRate, System.Threading.CancellationToken _) => clientRate);

        var controller = new SalesController(
            mockSalesService.Object,
            mockCurrentUserService.Object,
            null,
            null,
            new CheckoutCalculationService(mockSalesService.Object));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
        };

        var previewRequest = new CheckoutPreviewRequest
        {
            ExchangeRate = 50m,
            Payments = new List<SalePaymentDto>
            {
                new SalePaymentDto { PaymentMethodId = 1, Amount = 50m, AmountBsS = 2500m },
                new SalePaymentDto { PaymentMethodId = 2, Amount = 50m, AmountBsS = 2500.50m }
            }
        };

        var previewResult = await controller.GetCheckoutPreviewAsync(42, previewRequest);
        var previewOk = Assert.IsType<OkObjectResult>(previewResult.Result);
        var preview = Assert.IsType<CheckoutPreviewResponse>(previewOk.Value);

        // Arrange: completion real con los mismos pagos
        using var context = CreateInMemoryContext();
        var service = CreateSalesService(context);

        var sale = new Sale
        {
            Id = 103,
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
            new PaymentMethod { Id = 2, Name = "Punto de Venta", IsCash = false });
        await context.SaveChangesAsync();

        var payments = new List<PaymentInfo>
        {
            new PaymentInfo(1, 50m, 2500m, null),
            new PaymentInfo(2, 50m, 2500.50m, null)
        };

        // Act
        await service.CompleteSaleAsync(sale.Id, 50m, payments);

        // Assert
        var savedSale = await context.Sales.AsNoTracking().FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(0.50m, preview.RoundingAdjustment);
        Assert.Equal(preview.RoundingAdjustment, savedSale.RoundingAdjustment);
    }

    // 8.152 (SEC-08, REQ-CPR-01/02): con desvío > tolerancia el preview calcula TODOS los totales
    // con la tasa BCV anclada por el resolver compartido (servicio real + BCV 50; cliente envía 60).
    [Fact]
    public async Task SalesController_GetCheckoutPreview_AnchorsClientRateToBcv_WhenDeviationExceedsTolerance()
    {
        // Arrange
        using var context = CreateInMemoryContext();
        var inventory = new Mock<IInventoryService>();
        inventory.Setup(i => i.GetTodayExchangeRateAsync(It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(50m);
        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        context.Sales.Add(new Sale
        {
            Id = 42,
            TotalUSD = 100m,
            Subtotal = 100m,
            AppliedRate = 50m,
            TotalBsS = 5000m,
            SubtotalBsS = 5000m,
            Status = SaleStatus.Pending,
            Items = new List<SaleItem>()
        });
        await context.SaveChangesAsync();

        var service = new SalesService(
            context,
            inventory.Object,
            new Mock<IMediator>().Object,
            new Mock<ICashDrawerService>().Object,
            settings.Object);
        var controller = new SalesController(
            service,
            new Mock<ICurrentUserService>().Object,
            null,
            null,
            new CheckoutCalculationService(service));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
        };

        var previewRequest = new CheckoutPreviewRequest
        {
            ExchangeRate = 60m,
            Payments = new List<SalePaymentDto>
            {
                new SalePaymentDto { PaymentMethodId = 1, Amount = 100m }
            }
        };

        // Act
        var actionResult = await controller.GetCheckoutPreviewAsync(42, previewRequest);

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var preview = Assert.IsType<CheckoutPreviewResponse>(okResult.Value);
        Assert.Equal(5000m, preview.TotalBsS);     // 100 USD * 50 (BCV anclada), no 100 * 60
        Assert.Equal(5000m, preview.TotalPaidBsS); // el pago USD se convierte con la tasa anclada
        Assert.True(preview.IsFullyPaid);
    }

    // 8.152 (SEC-08, REQ-CPR-01/02): el rechazo ≥ ±100% fluye desde ResolveCheckoutRateAsync
    // (servicio real) y el controller NO lo captura: el middleware global lo mapea a 400.
    [Fact]
    public async Task SalesController_GetCheckoutPreview_PropagatesRateRejection_WhenDeviationReachesOneHundredPercent()
    {
        // Arrange
        using var context = CreateInMemoryContext();
        var inventory = new Mock<IInventoryService>();
        inventory.Setup(i => i.GetTodayExchangeRateAsync(It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(50m);
        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        context.Sales.Add(new Sale
        {
            Id = 43,
            TotalUSD = 100m,
            Subtotal = 100m,
            AppliedRate = 50m,
            TotalBsS = 5000m,
            SubtotalBsS = 5000m,
            Status = SaleStatus.Pending,
            Items = new List<SaleItem>()
        });
        await context.SaveChangesAsync();

        var service = new SalesService(
            context,
            inventory.Object,
            new Mock<IMediator>().Object,
            new Mock<ICashDrawerService>().Object,
            settings.Object);
        var controller = new SalesController(
            service,
            new Mock<ICurrentUserService>().Object,
            null,
            null,
            new CheckoutCalculationService(service));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
        };

        var previewRequest = new CheckoutPreviewRequest { ExchangeRate = 120m };

        // Act
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => controller.GetCheckoutPreviewAsync(43, previewRequest));

        // Assert: mensaje exacto del rechazo (el middleware global responde 400 ProblemDetails).
        Assert.Equal(
            "La tasa de cambio 120 fue rechazada: excede ±100% de la tasa BCV oficial (50). Contacte al supervisor.",
            ex.Message);
    }

    // 8.152 (SEC-08, REQ-CPR-01 punto 2): sin tasa del cliente el preview conserva el fallback a
    // sale.AppliedRate y NO invoca el resolver.
    [Fact]
    public async Task SalesController_GetCheckoutPreview_WithoutClientRate_UsesAppliedRateAndSkipsResolver()
    {
        // Arrange
        var mockSalesService = new Mock<ISalesService>();
        var mockCurrentUserService = new Mock<ICurrentUserService>();

        mockSalesService.Setup(s => s.GetSaleAsync(42)).ReturnsAsync(new SaleDto
        {
            Id = 42,
            TotalUSD = 100m,
            AppliedRate = 50m,
            Items = new List<SaleItemDto>()
        });

        var controller = new SalesController(
            mockSalesService.Object,
            mockCurrentUserService.Object,
            null,
            null,
            new CheckoutCalculationService(mockSalesService.Object));
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext()
        };

        // Act
        var actionResult = await controller.GetCheckoutPreviewAsync(42, new CheckoutPreviewRequest { ExchangeRate = 0m });

        // Assert
        var okResult = Assert.IsType<OkObjectResult>(actionResult.Result);
        var preview = Assert.IsType<CheckoutPreviewResponse>(okResult.Value);
        Assert.Equal(5000m, preview.TotalBsS);
        mockSalesService.Verify(
            s => s.ResolveCheckoutRateAsync(It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<System.Threading.CancellationToken>()),
            Times.Never);
    }
}
