using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;
using SalesService = Sales.Module.Services.SalesService;
using ICashDrawerService = Sales.Module.Interfaces.ICashDrawerService;
using CashDrawerStatus = Sales.Module.Entities.CashDrawerStatus;

namespace CommandCenter.Tests.Unit;

public class SaleItemCostSnapshotTests
{
    [Fact]
    public async Task AddItemAsync_PersistsUnitCostSnapshot_FromCatalogCost()
    {
        var context = TestDatabaseFactory.CreateSalesDbContext();
        var inventoryMock = new Mock<IInventoryService>();
        var mediatorMock = new Mock<MediatR.IMediator>();
        var cashDrawerMock = new Mock<ICashDrawerService>();
        var settingsMock = new Mock<Core.Interfaces.ISystemSettingsService>();

        cashDrawerMock
            .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new Sales.Module.DTOs.CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        var service = new SalesService(context, inventoryMock.Object, mediatorMock.Object, cashDrawerMock.Object, settingsMock.Object);
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var product = new SaleProductInfoDto
        {
            Id = 88,
            Name = "Con Costo",
            PriceUSD = 3.50m,
            PriceRetailUSD = 3.50m,
            CostPriceUSD = 2.50m
        };
        inventoryMock.Setup(i => i.GetSaleProductByIdAsync(88)).ReturnsAsync(product);

        var sale = await service.StartSaleAsync();
        await service.AddItemAsync(sale.Id, 88, 1, 842.21m);

        var item = context.Set<Sales.Module.Entities.SaleItem>().Single(i => i.ProductId == 88);

        // El costo del catalogo viaja con la linea: sin esto, el calculo de ganancias (Paso 14)
        // recalcularia COGS con el costo actual y violaria la inmutabilidad del historial.
        Assert.Equal(2.50m, item.UnitCostUSD);
        // Paso 14 (S4): la moneda local del costo se materializa solo al completar la venta.
        Assert.Null(item.UnitCostLocal);
    }

    [Fact]
    public async Task AddItemAsync_WhenCatalogCostIsUnknown_PersistsNullUnitCost()
    {
        var context = TestDatabaseFactory.CreateSalesDbContext();
        var inventoryMock = new Mock<IInventoryService>();
        var mediatorMock = new Mock<MediatR.IMediator>();
        var cashDrawerMock = new Mock<ICashDrawerService>();
        var settingsMock = new Mock<Core.Interfaces.ISystemSettingsService>();

        cashDrawerMock
            .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new Sales.Module.DTOs.CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        var service = new SalesService(context, inventoryMock.Object, mediatorMock.Object, cashDrawerMock.Object, settingsMock.Object);
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var product = new SaleProductInfoDto
        {
            Id = 89,
            Name = "Sin Costo",
            PriceUSD = 5m,
            PriceRetailUSD = 5m,
            CostPriceUSD = null
        };
        inventoryMock.Setup(i => i.GetSaleProductByIdAsync(89)).ReturnsAsync(product);

        var sale = await service.StartSaleAsync();
        await service.AddItemAsync(sale.Id, 89, 1, 842.21m);

        var item = context.Set<Sales.Module.Entities.SaleItem>().Single(i => i.ProductId == 89);

        Assert.Null(item.UnitCostUSD);
    }

    [Fact]
    public void SaleItemCost_IsNotExposedToClients()
    {
        // El costo es dato sensible: no debe filtrarse por ningun DTO que viaja al cliente
        // (regla de enmascaramiento de costos/margenes para cajeros).
        Assert.Null(typeof(SaleItemDto).GetProperty("UnitCostUSD", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase));
        Assert.Null(typeof(SaleDto).GetProperty("UnitCostUSD", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase));
    }

    private static Mock<IInventoryService> CreateInventoryWithCurrentCost(
        int productId,
        decimal retailPriceUsd,
        decimal currentCostUsd)
    {
        // El catalogo reporta un costo VIGENTE distinto: el snapshot local debe salir de la linea,
        // nunca del costo actual del producto.
        var inventoryMock = new Mock<IInventoryService>();
        inventoryMock
            .Setup(i => i.GetSaleProductsByIdsAsync(It.IsAny<IEnumerable<int>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<SaleProductInfoDto>
            {
                new()
                {
                    Id = productId,
                    Name = "Producto con costo snapshot",
                    PriceUSD = retailPriceUsd,
                    PriceRetailUSD = retailPriceUsd,
                    CostPriceUSD = currentCostUsd
                }
            });
        return inventoryMock;
    }

    private static SalesService CreateCheckoutService(
        SalesDbContext context,
        IInventoryService inventory,
        IMonetaryProfileService? monetaryProfileService = null,
        ICurrencyRateService? currencyRateService = null)
    {
        var cashDrawerMock = new Mock<ICashDrawerService>();
        cashDrawerMock
            .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        return new SalesService(
            context,
            inventory,
            new Mock<MediatR.IMediator>().Object,
            cashDrawerMock.Object,
            new Mock<ISystemSettingsService>().Object,
            monetaryProfileService: monetaryProfileService,
            currencyRateService: currencyRateService);
    }

    private static Mock<IMonetaryProfileService> CreateDualProfileMock(string secondaryCode)
    {
        var mock = new Mock<IMonetaryProfileService>();
        mock
            .Setup(p => p.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Dual,
                PrimaryCurrencyCode = "USD",
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = secondaryCode,
                SecondaryCurrencySymbol = secondaryCode,
                IsConfigured = true
            });
        return mock;
    }

    private static Mock<ICurrencyRateService> CreateManualRateMock(string secondaryCode, decimal rate)
    {
        var mock = new Mock<ICurrencyRateService>();
        mock
            .Setup(r => r.GetRateAsync("USD", secondaryCode, It.IsAny<DateOnly?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CurrencyRateQuote(rate, CurrencyRateSource.Manual, TimeZoneHelper.GetVenezuelaDate(), "USD", secondaryCode));
        return mock;
    }

    private static async Task<Sale> SeedPendingSaleWithCostSnapshotAsync(
        SalesDbContext context,
        decimal unitPriceUsd,
        decimal? unitCostUsd,
        int productId = 901)
    {
        var sale = new Sale
        {
            TotalUSD = unitPriceUsd,
            Subtotal = unitPriceUsd,
            AppliedRate = 50m,
            TotalBsS = unitPriceUsd * 50m,
            SubtotalBsS = unitPriceUsd * 50m,
            Status = SaleStatus.Pending
        };
        sale.Items.Add(new SaleItem
        {
            ProductId = productId,
            ProductName = "Producto con costo snapshot",
            Quantity = 1m,
            UnitPrice = unitPriceUsd,
            UnitPriceBsS = unitPriceUsd * 50m,
            Subtotal = unitPriceUsd,
            SubtotalBsS = unitPriceUsd * 50m,
            UnitCostUSD = unitCostUsd
        });

        context.Sales.Add(sale);
        await context.SaveChangesAsync();
        return sale;
    }

    [Fact]
    public async Task CompletedSale_DualUsdVes_MaterializesUnitCostLocalFromTheSnapshotRate()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var inventory = CreateInventoryWithCurrentCost(productId: 901, retailPriceUsd: 5m, currentCostUsd: 99m);
        var service = CreateCheckoutService(context, inventory.Object);
        var sale = await SeedPendingSaleWithCostSnapshotAsync(context, unitPriceUsd: 5m, unitCostUsd: 2.50m);

        await service.CompleteSaleAsync(sale.Id, 40m, new List<PaymentInfo> { new(3, 5m, 200m, null) });

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal(40m, saved.AppliedRate);

        var item = await context.SaleItems.AsNoTracking().SingleAsync(i => i.SaleId == sale.Id);
        Assert.Equal(2.50m, item.UnitCostUSD);
        // 2.50 USD x 40 (tasa snapshot de la venta) = 100.0000 en la moneda local.
        Assert.Equal(100.0000m, item.UnitCostLocal);
    }

    [Fact]
    public async Task CompletedSale_SingleNonUsdProfile_MaterializesUnitCostLocalInThePrimaryCurrency()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = 50,
            Name = "Punto de Venta COP",
            IsCash = false,
            IsActive = true,
            Currency = "COP"
        });
        await context.SaveChangesAsync();

        var profile = new Mock<IMonetaryProfileService>();
        profile
            .Setup(p => p.GetAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new MonetaryProfileDto
            {
                Mode = MonetaryMode.Single,
                PrimaryCurrencyCode = "COP",
                PrimaryCurrencySymbol = "$",
                SecondaryCurrencyCode = null,
                SecondaryCurrencySymbol = null,
                IsConfigured = true
            });

        var inventory = CreateInventoryWithCurrentCost(productId: 901, retailPriceUsd: 5m, currentCostUsd: 99m);
        var service = CreateCheckoutService(context, inventory.Object, profile.Object);
        var sale = await SeedPendingSaleWithCostSnapshotAsync(context, unitPriceUsd: 5m, unitCostUsd: 2.50m);

        await service.CompleteSaleAsync(sale.Id, 50m, new List<PaymentInfo> { new(50, 5m, 5m, null) });

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        Assert.Equal("Single", saved.MonetaryMode);
        Assert.Equal(1m, saved.AppliedRate);

        var item = await context.SaleItems.AsNoTracking().SingleAsync(i => i.SaleId == sale.Id);
        Assert.Equal(2.50m, item.UnitCostUSD);
        // Single es identidad: el snapshot local es el costo USD x AppliedRate (1).
        Assert.Equal(2.50m, item.UnitCostLocal);
    }

    [Fact]
    public async Task CompletedSale_UnknownUnitCost_KeepsUnitCostLocalNull()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);

        var inventory = CreateInventoryWithCurrentCost(productId: 901, retailPriceUsd: 5m, currentCostUsd: 99m);
        var service = CreateCheckoutService(context, inventory.Object);
        var sale = await SeedPendingSaleWithCostSnapshotAsync(context, unitPriceUsd: 5m, unitCostUsd: null);

        await service.CompleteSaleAsync(sale.Id, 40m, new List<PaymentInfo> { new(3, 5m, 200m, null) });

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);

        var item = await context.SaleItems.AsNoTracking().SingleAsync(i => i.SaleId == sale.Id);
        // Costo desconocido: no se inventa ni se backfillea con el costo vigente del catalogo.
        Assert.Null(item.UnitCostUSD);
        Assert.Null(item.UnitCostLocal);
    }

    [Fact]
    public async Task CompletedSale_ManualPairFractionalRate_RoundsUnitCostLocalToFourDecimalsAwayFromZero()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        await TestDatabaseFactory.SeedStandardSalesDataAsync(context);
        context.PaymentMethods.Add(new PaymentMethod
        {
            Id = 51,
            Name = "Punto de Venta COP",
            IsCash = false,
            IsActive = true,
            Currency = "COP"
        });
        await context.SaveChangesAsync();

        const decimal rate = 40.1235m;
        var inventory = CreateInventoryWithCurrentCost(productId: 901, retailPriceUsd: 5m, currentCostUsd: 99m);
        var service = CreateCheckoutService(
            context,
            inventory.Object,
            CreateDualProfileMock("COP").Object,
            CreateManualRateMock("COP", rate).Object);
        var sale = await SeedPendingSaleWithCostSnapshotAsync(context, unitPriceUsd: 5m, unitCostUsd: 2.53m);

        // 5.00 x 40.1235 = 200.6175 -> techo 2dp del efectivo local de la linea = 200.62.
        await service.CompleteSaleAsync(sale.Id, rate, new List<PaymentInfo> { new(51, 5m, 200.62m, null) });

        var saved = await context.Sales.AsNoTracking().SingleAsync(s => s.Id == sale.Id);
        Assert.Equal(SaleStatus.Completed, saved.Status);
        // El par manual conserva la tasa 4dp exacta como snapshot de la venta.
        Assert.Equal(rate, saved.AppliedRate);

        var item = await context.SaleItems.AsNoTracking().SingleAsync(i => i.SaleId == sale.Id);
        // 2.53 x 40.1235 = 101.512455 -> 4dp AwayFromZero = 101.5125 (ToEven daria 101.5124).
        Assert.Equal(101.5125m, item.UnitCostLocal);
    }
}
