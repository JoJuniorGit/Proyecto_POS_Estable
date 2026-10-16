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

    // =============================================================================================
    // 8.185-T3 (S4/S5): gate de cobertura DUAL-DOMINIO del completar venta. Un pago que liquida el
    // total EFECTIVO Bs.S (derivado por línea con la política cash vigente) no se rechaza aunque
    // difiera del canónico; el rechazo exige déficit en AMBOS dominios (USD > 0.05 y Bs.S efectivo
    // > 0.05 × tasa). Los snapshots quedan consistentes con el total efectivo.
    // =============================================================================================

    private static (SalesService Service, Mock<ICashDrawerService> CashDrawer) CreateSalesServiceWithCashPolicy(
        SalesDbContext context,
        string? neverRoundDownSetting)
    {
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();

        mockSettings
            .Setup(s => s.GetSettingAsync(It.IsAny<string>()))
            .ReturnsAsync((string?)null);
        if (neverRoundDownSetting != null)
        {
            mockSettings
                .Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.Pricing.DefaultEnforceMinimumMarginFloor))
                .ReturnsAsync(neverRoundDownSetting);
        }

        mockCashDrawer
            .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        var service = new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);
        return (service, mockCashDrawer);
    }

    private static void SeedCheckoutPaymentMethods(SalesDbContext context) =>
        context.PaymentMethods.AddRange(
            new PaymentMethod { Id = 1, Name = "Efectivo", IsCash = true },
            new PaymentMethod { Id = 2, Name = "Punto de Venta", IsCash = false });

    private static Sale BuildPolicySale(int id, params (decimal UnitPrice, decimal Quantity, RoundingRuleType? Rule)[] lines)
    {
        var sale = new Sale
        {
            Id = id,
            Status = SaleStatus.Pending
        };

        int productId = id * 100;
        foreach (var (unitPrice, quantity, rule) in lines)
        {
            productId++;
            sale.Items.Add(new SaleItem
            {
                SaleId = id,
                ProductId = productId,
                ProductName = $"Item {productId}",
                UnitPrice = unitPrice,
                Quantity = quantity,
                RoundingRuleCashBs = rule
            });
        }

        return sale;
    }

    // Caso 1: toggle ON (never-down), pago exacto del efectivo por línea.
    [Fact]
    public async Task CompleteSale_DualDomainGate_NeverRoundDown_ExactEffectiveCashPayment_CompletesWithEffectiveSnapshots()
    {
        using var context = CreateInMemoryContext();
        var (service, _) = CreateSalesServiceWithCashPolicy(context, neverRoundDownSetting: "true");
        SeedCheckoutPaymentMethods(context);

        // 1.35 USD * 10 = 13.50 -> escalón entero con never-down => 14.00 (canónico: 13.50).
        var sale = BuildPolicySale(300, (1.35m, 1m, RoundingRuleType.VesCashFriendlyInteger));
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var payments = new List<PaymentInfo> { new PaymentInfo(1, 0m, 14.00m, null) };

        await service.CompleteSaleAsync(sale.Id, 10m, payments);

        var saved = await context.Sales.AsNoTracking().Include(s => s.Payments).FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal(1.35m, saved.TotalUSD);
        Assert.Equal(14.00m, saved.TotalBsS);
        Assert.Equal(14.00m, saved.FinalPaidAmountBsS);
        Assert.Equal(0m, saved.RoundingAdjustment);
        Assert.Equal(1.40m, saved.Payments.Single().Amount);
        Assert.Equal(14.00m, saved.Payments.Single().AmountBsS);
    }

    // Caso 2: toggle ON multi-línea con escalones acumulados; el pago exacto del efectivo pasa,
    // NO genera vuelto y deja el escalón de la política en caja (caja neta = efectivo).
    [Fact]
    public async Task CompleteSale_DualDomainGate_NeverRoundDown_MultiLineAccumulatedSteps_ExactEffectivePaymentCompletes()
    {
        using var context = CreateInMemoryContext();
        var (service, cashDrawer) = CreateSalesServiceWithCashPolicy(context, neverRoundDownSetting: "true");
        SeedCheckoutPaymentMethods(context);

        // 3 líneas de 1.04 USD a tasa 10: matemático 10.40 -> escalón 11.00 por línea (never-down);
        // el efectivo acumulado 33.00 supera al canónico 31.20 (TotalUSD 3.12).
        var sale = BuildPolicySale(
            301,
            (1.04m, 1m, RoundingRuleType.VesCashFriendlyInteger),
            (1.04m, 1m, RoundingRuleType.VesCashFriendlyInteger),
            (1.04m, 1m, RoundingRuleType.VesCashFriendlyInteger));
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var payments = new List<PaymentInfo> { new PaymentInfo(1, 0m, 33.00m, null) };

        await service.CompleteSaleAsync(sale.Id, 10m, payments);

        var saved = await context.Sales.AsNoTracking().Include(s => s.Payments).FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal(3.12m, saved.TotalUSD);
        Assert.Equal(33.00m, saved.TotalBsS);
        Assert.Equal(33.00m, saved.FinalPaidAmountBsS);
        Assert.Equal(0m, saved.RoundingAdjustment);

        // Vuelto dual: pagar EXACTO el efectivo publicado no devuelve el escalón (antes se medía
        // contra el canónico y devolvía 1.80 Bs.S); la caja recibe el efectivo completo.
        cashDrawer.Verify(
            c => c.RecordSaleChangeAsync(
                It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>(),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<decimal>(), It.IsAny<System.Threading.CancellationToken>()),
            Times.Never);

        var incomeTx = await context.CashTransactions
            .Where(t => t.SaleId == sale.Id && t.Type == CashTransactionType.Income)
            .ToListAsync();
        Assert.Single(incomeTx);
        Assert.Equal(33.00m, incomeTx[0].AmountLocal);
    }

    // Caso 2b: toggle ON, pago por ENCIMA del efectivo -> vuelto = exceso contra el efectivo
    // (no contra el canónico: el escalón de la política queda en caja).
    [Fact]
    public async Task CompleteSale_DualVuelto_NeverRoundDown_OverpaymentAboveEffective_ReturnsExcessOnly()
    {
        using var context = CreateInMemoryContext();
        var (service, cashDrawer) = CreateSalesServiceWithCashPolicy(context, neverRoundDownSetting: "true");
        SeedCheckoutPaymentMethods(context);

        // Misma venta: efectivo 33.00, canónico 31.20, TotalUSD 3.12 a tasa 10.
        var sale = BuildPolicySale(
            310,
            (1.04m, 1m, RoundingRuleType.VesCashFriendlyInteger),
            (1.04m, 1m, RoundingRuleType.VesCashFriendlyInteger),
            (1.04m, 1m, RoundingRuleType.VesCashFriendlyInteger));
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        // El cliente entrega 40.00 Bs.S en efectivo: el vuelto real es 40.00 - 33.00 = 7.00 Bs.S
        // (la fórmula previa contra el canónico devolvía 8.80 y regalaba el escalón).
        var payments = new List<PaymentInfo> { new PaymentInfo(1, 0m, 40.00m, null) };

        await service.CompleteSaleAsync(sale.Id, 10m, payments);

        var saved = await context.Sales.AsNoTracking().Include(s => s.Payments).FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal(33.00m, saved.TotalBsS);
        Assert.Equal(40.00m, saved.FinalPaidAmountBsS);
        Assert.Equal(7.00m, saved.RoundingAdjustment);
        cashDrawer.Verify(
            c => c.RecordSaleChangeAsync(1, 0.70m, 7.00m, 10m, It.IsAny<string>(), sale.Id, 1, It.IsAny<decimal>(), It.IsAny<System.Threading.CancellationToken>()),
            Times.Once);
    }

    // Caso 3: toggle OFF (nearest puede bajar) con pago exacto del efectivo. Este es el caso que el
    // gate USD-only previo rechazaba (remanente USD > 0.05 con cobertura Bs.S completa).
    [Fact]
    public async Task CompleteSale_DualDomainGate_NearestRoundDown_ExactEffectivePaymentCompletes_WhereUsdOnlyGateWouldReject()
    {
        using var context = CreateInMemoryContext();
        var (service, _) = CreateSalesServiceWithCashPolicy(context, neverRoundDownSetting: "false");
        SeedCheckoutPaymentMethods(context);

        // 2 líneas de 1.44 USD a tasa 10: matemático 14.40 -> nearest 14.00 (baja); canónico 14.40.
        var sale = BuildPolicySale(
            302,
            (1.44m, 1m, RoundingRuleType.VesCashFriendlyInteger),
            (1.44m, 1m, RoundingRuleType.VesCashFriendlyInteger));
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var payments = new List<PaymentInfo> { new PaymentInfo(1, 0m, 28.00m, null) };

        // Precondición documentada: el pago exacto del total efectivo queda 0.08 USD por debajo del
        // total canónico, de modo que el gate USD-only previo (remanente USD > 0.05) lo rechazaba.
        Assert.True(2.88m - Math.Round(28.00m / 10m, 2, MidpointRounding.AwayFromZero) > 0.05m);

        await service.CompleteSaleAsync(sale.Id, 10m, payments);

        var saved = await context.Sales.AsNoTracking().Include(s => s.Payments).FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal(2.88m, saved.TotalUSD);
        Assert.Equal(28.00m, saved.TotalBsS);
        Assert.Equal(28.00m, saved.FinalPaidAmountBsS);
        Assert.Equal(0m, saved.RoundingAdjustment);
    }

    // Caso 4: subpago real (déficit en ambos dominios) sigue rechazándose con el mensaje de siempre.
    [Theory]
    [InlineData(0, 12.00)]  // pago Bs.S corto: 12.00 contra un efectivo de 14.00
    [InlineData(1.00, 0)]   // pago USD corto: 1.00 contra un TotalUSD de 1.35
    public async Task CompleteSale_DualDomainGate_RealUnderpaymentInBothDomains_IsRejected(decimal amountUsd, decimal amountLocal)
    {
        using var context = CreateInMemoryContext();
        var (service, _) = CreateSalesServiceWithCashPolicy(context, neverRoundDownSetting: "true");
        SeedCheckoutPaymentMethods(context);

        // Efectivo por línea 14.00 (TotalUSD 1.35 a tasa 10); tolerancia Bs.S = 0.05 * 10 = 0.50.
        var sale = BuildPolicySale(303, (1.35m, 1m, RoundingRuleType.VesCashFriendlyInteger));
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var payments = new List<PaymentInfo> { new PaymentInfo(1, amountUsd, amountLocal, null) };

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.CompleteSaleAsync(sale.Id, 10m, payments));
        Assert.Contains("no cubre la totalidad de la venta", ex.Message);

        context.ChangeTracker.Clear();
        var saved = await context.Sales.AsNoTracking().FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Pending, saved.Status);
    }

    // Caso 5: el pago USD exacto del TotalUSD pasa aunque el total efectivo Bs.S lo exceda.
    [Fact]
    public async Task CompleteSale_DualDomainGate_UsdExactPaymentOfTotalUsd_Passes_EvenWhenEffectiveBsSExceedsIt()
    {
        using var context = CreateInMemoryContext();
        var (service, cashDrawer) = CreateSalesServiceWithCashPolicy(context, neverRoundDownSetting: "true");
        SeedCheckoutPaymentMethods(context);

        // 2 unidades de 1.35 USD: efectivo por unidad 14.00 -> TotalBsS 28.00; TotalUSD 2.70.
        var sale = BuildPolicySale(304, (1.35m, 2m, RoundingRuleType.VesCashFriendlyInteger));
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        // Efectivo USD exacto: 2.70 -> 27.00 Bs.S a tasa 10 (monto local entero, válido para cash).
        var payments = new List<PaymentInfo> { new PaymentInfo(1, 2.70m, 0m, null) };

        await service.CompleteSaleAsync(sale.Id, 10m, payments);

        var saved = await context.Sales.AsNoTracking().Include(s => s.Payments).FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal(28.00m, saved.TotalBsS);
        Assert.Equal(27.00m, saved.FinalPaidAmountBsS);
        // Comportamiento definido (vuelto dual): el pago USD cubre el TotalUSD exacto pero acredita
        // 27.00 Bs.S < 28.00 efectivo; NO hay sobrepago contra el efectivo -> sin vuelto, y el ajuste
        // literal RoundingAdjustment = paidBsS − TotalBsS queda -1.00 (diferencia canónico/efectivo).
        Assert.Equal(-1.00m, saved.RoundingAdjustment);
        cashDrawer.Verify(
            c => c.RecordSaleChangeAsync(
                It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>(),
                It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<decimal>(), It.IsAny<System.Threading.CancellationToken>()),
            Times.Never);
    }

    // Caso 6: vuelto/efectivo redondeado con la regla de siempre (línea ExactMath, pago entero).
    [Fact]
    public async Task CompleteSale_CashRoundedChange_CapturesVueltoAndRoundingAdjustment()
    {
        using var context = CreateInMemoryContext();
        var (service, cashDrawer) = CreateSalesServiceWithCashPolicy(context, neverRoundDownSetting: null);
        SeedCheckoutPaymentMethods(context);

        // Línea sin regla cash (ExactMath): 1.44 * 10 = 14.40 canónico; el efectivo solo acepta
        // enteros, el cliente paga 15.00 y el residual 0.60 vuelve como vuelto.
        var sale = BuildPolicySale(305, (1.44m, 1m, null));
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var payments = new List<PaymentInfo> { new PaymentInfo(1, 0m, 15.00m, null) };

        await service.CompleteSaleAsync(sale.Id, 10m, payments);

        var saved = await context.Sales.AsNoTracking().Include(s => s.Payments).FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal(14.40m, saved.TotalBsS);
        Assert.Equal(15.00m, saved.FinalPaidAmountBsS);
        Assert.Equal(0.60m, saved.RoundingAdjustment);
        cashDrawer.Verify(
            c => c.RecordSaleChangeAsync(1, 0.06m, 0.60m, 10m, It.IsAny<string>(), sale.Id, 1, It.IsAny<decimal>(), It.IsAny<System.Threading.CancellationToken>()),
            Times.Once);
    }

    // Caso 7: ExactMath (default) conserva los umbrales legados del gate (tolerancia 0.05 USD).
    [Theory]
    [InlineData(0.76, true)]   // remanente USD exactamente 0.05 -> dentro de tolerancia
    [InlineData(0.75, false)]  // remanente 0.06 -> déficit en ambos dominios
    public async Task CompleteSale_ExactMathDefault_KeepsLegacyToleranceBoundary(decimal paidUsd, bool completes)
    {
        using var context = CreateInMemoryContext();
        var (service, cashDrawer) = CreateSalesServiceWithCashPolicy(context, neverRoundDownSetting: null);
        SeedCheckoutPaymentMethods(context);

        // Línea sin regla cash: techo canónico 8.104. 0.81 * 842.21 = 682.1901 -> 682.20.
        var sale = BuildPolicySale(306, (0.81m, 1m, null));
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var payments = new List<PaymentInfo> { new PaymentInfo(2, paidUsd, 0m, null) };

        if (completes)
        {
            await service.CompleteSaleAsync(sale.Id, 842.21m, payments);
            var saved = await context.Sales.AsNoTracking().FirstAsync(s => s.Id == sale.Id);
            Assert.Equal(SaleStatus.Completed, saved.Status);
            Assert.Equal(682.20m, saved.TotalBsS);
            // ExactMath sin vuelto: el pago queda por debajo del total (efectivo == canónico).
            cashDrawer.Verify(
                c => c.RecordSaleChangeAsync(
                    It.IsAny<int>(), It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>(),
                    It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int?>(), It.IsAny<decimal>(), It.IsAny<System.Threading.CancellationToken>()),
                Times.Never);
        }
        else
        {
            await Assert.ThrowsAsync<ArgumentException>(() => service.CompleteSaleAsync(sale.Id, 842.21m, payments));
        }
    }

    // Caso 8: mixto USD+Bs.S: el remanente queda consistente, sin decimales añadidos por la
    // diferencia entre tasa canónica y precio redondeado.
    [Fact]
    public async Task CompleteSale_MixedUsdAndBsS_RemainderConsistentWithoutAddedDecimals()
    {
        using var context = CreateInMemoryContext();
        var (service, _) = CreateSalesServiceWithCashPolicy(context, neverRoundDownSetting: null);
        SeedCheckoutPaymentMethods(context);

        // Línea ExactMath 1.35 USD a tasa 10 -> TotalBsS 13.50.
        var sale = BuildPolicySale(307, (1.35m, 1m, null));
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var payments = new List<PaymentInfo>
        {
            new PaymentInfo(1, 1.00m, 0m, null),   // efectivo USD: 10.00 Bs.S exactos a tasa 10
            new PaymentInfo(2, 0m, 3.50m, null)    // Pago Móvil: 3.50 Bs.S
        };

        await service.CompleteSaleAsync(sale.Id, 10m, payments);

        var saved = await context.Sales.AsNoTracking().Include(s => s.Payments).FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal(2, saved.Payments.Count);
        Assert.Equal(1.35m, saved.Payments.Sum(p => p.Amount));
        Assert.Equal(13.50m, saved.Payments.Sum(p => p.AmountBsS));
        Assert.Equal(13.50m, saved.FinalPaidAmountBsS);
        Assert.Equal(0m, saved.RoundingAdjustment);
    }

    // Apartado en custodia: el mismo gate dual aplica a la exigencia de pago al 100%.
    [Fact]
    public async Task CompleteSale_DualDomainGate_PendingPickupPaidAtEffectivePrice_Completes()
    {
        using var context = CreateInMemoryContext();
        var (service, _) = CreateSalesServiceWithCashPolicy(context, neverRoundDownSetting: "false");
        SeedCheckoutPaymentMethods(context);

        context.Customers.Add(new Customer
        {
            Id = 7,
            Name = "María Pérez",
            CedulaOrRif = "V-12345678",
            Phone = "0414-1234567",
            IsDefault = false
        });

        var sale = BuildPolicySale(
            308,
            (1.44m, 1m, RoundingRuleType.VesCashFriendlyInteger),
            (1.44m, 1m, RoundingRuleType.VesCashFriendlyInteger));
        sale.CustomerId = 7;
        sale.CustomerName = "María Pérez";
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var payments = new List<PaymentInfo> { new PaymentInfo(1, 0m, 28.00m, null) };

        await service.CompleteSaleAsync(sale.Id, 10m, payments, isPendingPickup: true);

        var saved = await context.Sales.AsNoTracking().FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal(SaleDeliveryStatus.PendingPickup, saved.DeliveryStatus);
        Assert.Equal(28.00m, saved.TotalBsS);
    }

    // Paridad preview ↔ completo (S5): mismo total efectivo y misma cobertura dual para el mismo
    // conjunto de pagos.
    [Fact]
    public async Task CheckoutPreview_And_CompleteSale_AgreeOnEffectiveTotal_AndDualCoverage()
    {
        using var context = CreateInMemoryContext();
        var (service, _) = CreateSalesServiceWithCashPolicy(context, neverRoundDownSetting: "false");
        SeedCheckoutPaymentMethods(context);

        var sale = BuildPolicySale(
            309,
            (1.44m, 1m, RoundingRuleType.VesCashFriendlyInteger),
            (1.44m, 1m, RoundingRuleType.VesCashFriendlyInteger));
        // Estado persistido real tras agregar los ítems (AddItem → RecalculateTotalAsync).
        sale.AppliedRate = 10m;
        sale.Subtotal = 2.88m;
        sale.TotalUSD = 2.88m;
        sale.SubtotalBsS = 28.00m;
        sale.TotalBsS = 28.00m;
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var settings = new Mock<ISystemSettingsService>();
        settings.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((string?)null);
        settings.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.Pricing.DefaultEnforceMinimumMarginFloor)).ReturnsAsync("false");

        var calculator = new CheckoutCalculationService(service, settings.Object);
        var preview = await calculator.CalculatePreviewAsync(sale.Id, new CheckoutPreviewRequest
        {
            ExchangeRate = 10m,
            Payments = new List<SalePaymentDto> { new SalePaymentDto { PaymentMethodId = 1, AmountBsS = 28.00m } }
        });

        Assert.Equal(28.00m, preview.TotalBsS);
        Assert.True(preview.IsFullyPaid);

        await service.CompleteSaleAsync(sale.Id, 10m, new List<PaymentInfo> { new PaymentInfo(1, 0m, 28.00m, null) });

        var saved = await context.Sales.AsNoTracking().FirstAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal(preview.TotalBsS, saved.TotalBsS);
        Assert.Equal(28.00m, saved.FinalPaidAmountBsS);
    }
}
