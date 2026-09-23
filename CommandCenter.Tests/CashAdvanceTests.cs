using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClientCashService = Desktop.Client.Services;
using Microsoft.Extensions.DependencyInjection;
using Core.Entities;
using Core.Interfaces;
using Desktop.Client.ViewModels;
using Inventory.Module.Data;
using Inventory.Module.Services;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using ServerCashService = Sales.Module.Services;
using Xunit;
using Core.DTOs;
using SalesService = Sales.Module.Services.SalesService;
using ISalesService = Sales.Module.Interfaces.ISalesService;
using ICashDrawerService = Sales.Module.Interfaces.ICashDrawerService;
using CashDrawerStatus = Sales.Module.Entities.CashDrawerStatus;

namespace CommandCenter.Tests;

public class CashAdvanceTests
{
    private SalesDbContext GetInMemoryDbContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        return new SalesDbContext(options);
    }

    private InventoryDbContext GetInMemoryInventoryDbContext()
    {
        var options = new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        return new InventoryDbContext(options);
    }

    private ServerCashService.CashAdvanceCoordinator CreateCoordinatorWithSalesService(SalesDbContext context, ISystemSettingsService? settingsService = null)
    {
        var inventoryMock = new Mock<IInventoryService>();
        inventoryMock.Setup(i => i.GetCashAdvanceProductAsync())
            .ReturnsAsync(new SaleProductInfoDto { Id = 1, Name = "Adelanto de Efectivo", IsCashAdvance = true });

        var cashDrawerMock = new Mock<ICashDrawerService>();
        cashDrawerMock.Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        var mediatorMock = new Mock<IMediator>();
        var settings = settingsService ?? new Mock<ISystemSettingsService>().Object;

        var salesService = new SalesService(context, inventoryMock.Object, mediatorMock.Object, cashDrawerMock.Object, settings);
        var drawerService = new ServerCashService.CashDrawerService(context);

        return new ServerCashService.CashAdvanceCoordinator(context, salesService, drawerService, settings);
    }

    private ServerCashService.CashAdvanceCoordinator CreateCoordinatorWithSettings(SalesDbContext context, ISystemSettingsService settingsService)
    {
        var inventoryMock = new Mock<IInventoryService>();
        inventoryMock.Setup(i => i.GetCashAdvanceProductAsync())
            .ReturnsAsync(new SaleProductInfoDto { Id = 1, Name = "Adelanto de Efectivo", IsCashAdvance = true });

        var cashDrawerMock = new Mock<ICashDrawerService>();
        cashDrawerMock.Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        var mediatorMock = new Mock<IMediator>();

        var salesService = new SalesService(context, inventoryMock.Object, mediatorMock.Object, cashDrawerMock.Object, settingsService);
        var drawerService = new ServerCashService.CashDrawerService(context);

        return new ServerCashService.CashAdvanceCoordinator(context, salesService, drawerService, settingsService);
    }

    [Fact]
    public async Task ProcessCashAdvance_Transfer_Applies7PercentCommission_And_DeductsOnlyRequestedFromPhysicalDrawer()
    {
        using var context = GetInMemoryDbContext();
        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct))
            .ReturnsAsync("7.0");
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceCashCommissionPct))
            .ReturnsAsync("10.0");
        var coordinator = CreateCoordinatorWithSettings(context, settingsMock.Object);
        var drawerService = new ServerCashService.CashDrawerService(context);

        var session = await drawerService.OpenSessionAsync(2000m, 50.0m);

        var result = await coordinator.ProcessAsync(
            sessionId: session.Id,
            requestedAmountLocal: 1000m,
            paymentMethodId: 2,
            paymentMethodName: "Transferencia Bancaria",
            isTransfer: true,
            exchangeRate: 50.0m
        );

        Assert.Equal(1000m, result.RequestedAmountLocal);
        Assert.Equal(70m, result.CommissionAmountLocal);
        Assert.Equal(1070m, result.TotalChargedLocal);
        Assert.Equal(7.0m, result.CommissionPercentage);

        Assert.True(result.ExpenseTransaction.IsPhysicalCash);
        Assert.Equal(CashTransactionType.Expense, result.ExpenseTransaction.Type);
        Assert.Equal(1000m, result.ExpenseTransaction.AmountLocal);

        Assert.False(result.IncomeTransaction.IsPhysicalCash);
        Assert.Equal(CashTransactionType.Income, result.IncomeTransaction.Type);
        Assert.Equal(70m, result.IncomeTransaction.AmountLocal);

        var physicalBalance = await drawerService.GetCurrentBalanceLocalAsync(session.Id);
        Assert.Equal(1000m, physicalBalance);
    }

    [Fact]
    public async Task ProcessCashAdvance_GeneratesCompletedSale_WithConsecutiveInvoiceNumber()
    {
        using var context = GetInMemoryDbContext();
        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct))
            .ReturnsAsync("7.0");
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceCashCommissionPct))
            .ReturnsAsync("10.0");
        var coordinator = CreateCoordinatorWithSalesService(context, settingsMock.Object);
        var drawerService = new ServerCashService.CashDrawerService(context);

        var session = await drawerService.OpenSessionAsync(2000m, 50.0m);

        var result = await coordinator.ProcessAsync(
            sessionId: session.Id,
            requestedAmountLocal: 1000m,
            paymentMethodId: 2,
            paymentMethodName: "Transferencia Bancaria",
            isTransfer: true,
            exchangeRate: 50.0m
        );

        Assert.NotNull(result.RelatedSaleId);
        Assert.NotNull(result.InvoiceNumber);
        Assert.True(result.InvoiceNumber.Value > 0);

        var sale = await context.Sales
            .Include(s => s.Items)
            .Include(s => s.Payments)
            .FirstOrDefaultAsync(s => s.Id == result.RelatedSaleId.Value);

        Assert.NotNull(sale);
        Assert.Equal(SaleStatus.Completed, sale.Status);
        Assert.Equal(SaleDeliveryStatus.Delivered, sale.DeliveryStatus);
        Assert.Equal(1070m, sale.TotalBsS);
        Assert.Equal(21.4m, sale.TotalUSD);
        Assert.Single(sale.Items);
        Assert.Single(sale.Payments);
    }

    [Fact]
    public async Task ProcessCashAdvance_DoesNotCreateAdditionalCashTransactions()
    {
        using var context = GetInMemoryDbContext();
        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct))
            .ReturnsAsync("7.0");
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceCashCommissionPct))
            .ReturnsAsync("10.0");
        var coordinator = CreateCoordinatorWithSettings(context, settingsMock.Object);
        var drawerService = new ServerCashService.CashDrawerService(context);

        var session = await drawerService.OpenSessionAsync(2000m, 50.0m);

        await coordinator.ProcessAsync(
            sessionId: session.Id,
            requestedAmountLocal: 1000m,
            paymentMethodId: 2,
            paymentMethodName: "Transferencia",
            isTransfer: true,
            exchangeRate: 50.0m
        );

        var sessionTxCount = await context.CashTransactions.CountAsync(t => t.SessionId == session.Id);
        Assert.Equal(3, sessionTxCount);

        var cashAdvanceTxCount = await context.CashTransactions.CountAsync(t => t.Source == CashTransactionSource.CashAdvance);
        Assert.Equal(2, cashAdvanceTxCount);
    }

    [Fact]
    public async Task ProcessCashAdvance_SaleIntegration_DoesNotAffectDrawerBalanceTwice()
    {
        using var context = GetInMemoryDbContext();
        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct))
            .ReturnsAsync("7.0");
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceCashCommissionPct))
            .ReturnsAsync("10.0");
        var coordinator = CreateCoordinatorWithSettings(context, settingsMock.Object);
        var drawerService = new ServerCashService.CashDrawerService(context);

        var session = await drawerService.OpenSessionAsync(2000m, 50.0m);

        await coordinator.ProcessAsync(
            sessionId: session.Id,
            requestedAmountLocal: 1000m,
            paymentMethodId: 2,
            paymentMethodName: "Transferencia",
            isTransfer: true,
            exchangeRate: 50.0m
        );

        var physicalBalance = await drawerService.GetCurrentBalanceLocalAsync(session.Id);
        Assert.Equal(1000m, physicalBalance);
    }

    [Fact]
    public async Task ProcessCashAdvance_SaleItem_HasExplicitPriceOverridingProductDefault()
    {
        using var context = GetInMemoryDbContext();
        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct))
            .ReturnsAsync("7.0");
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceCashCommissionPct))
            .ReturnsAsync("10.0");
        var coordinator = CreateCoordinatorWithSalesService(context, settingsMock.Object);
        var drawerService = new ServerCashService.CashDrawerService(context);

        var session = await drawerService.OpenSessionAsync(2000m, 50.0m);

        var result = await coordinator.ProcessAsync(
            sessionId: session.Id,
            requestedAmountLocal: 1000m,
            paymentMethodId: 2,
            paymentMethodName: "Transferencia Bancaria",
            isTransfer: true,
            exchangeRate: 50.0m
        );

        var saleItem = await context.SaleItems.FirstOrDefaultAsync(si => si.SaleId == result.RelatedSaleId);
        Assert.NotNull(saleItem);
        Assert.Equal(1070m, saleItem.UnitPriceBsS);
        Assert.Equal(1070m, saleItem.SubtotalBsS);
        Assert.Equal(21.4m, saleItem.UnitPrice);
        Assert.Equal(21.4m, saleItem.Subtotal);
    }

    [Fact]
    public async Task ProcessCashAdvance_POS_Applies10PercentCommission()
    {
        using var context = GetInMemoryDbContext();
        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct))
            .ReturnsAsync("7.0");
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceCashCommissionPct))
            .ReturnsAsync("10.0");
        var coordinator = CreateCoordinatorWithSettings(context, settingsMock.Object);
        var drawerService = new ServerCashService.CashDrawerService(context);

        var session = await drawerService.OpenSessionAsync(2000m, 50.0m);

        var result = await coordinator.ProcessAsync(
            sessionId: session.Id,
            requestedAmountLocal: 1000m,
            paymentMethodId: 3,
            paymentMethodName: "Punto de Venta",
            isTransfer: false,
            exchangeRate: 50.0m
        );

        Assert.Equal(1000m, result.RequestedAmountLocal);
        Assert.Equal(100m, result.CommissionAmountLocal);
        Assert.Equal(1100m, result.TotalChargedLocal);
        Assert.Equal(10.0m, result.CommissionPercentage);
    }

    [Fact]
    public async Task ProcessCashAdvance_Transfer_UsesConfiguredCommissionFromSystemSettings()
    {
        using var context = GetInMemoryDbContext();

        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct))
            .ReturnsAsync("5.5");
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceCashCommissionPct))
            .ReturnsAsync("10.0");

        var coordinator = CreateCoordinatorWithSettings(context, settingsMock.Object);
        var drawerService = new ServerCashService.CashDrawerService(context);
        var session = await drawerService.OpenSessionAsync(2000m, 50.0m);

        var result = await coordinator.ProcessAsync(
            sessionId: session.Id,
            requestedAmountLocal: 1000m,
            paymentMethodId: 2,
            paymentMethodName: "Transferencia Bancaria",
            isTransfer: true,
            exchangeRate: 50.0m
        );

        Assert.Equal(55m, result.CommissionAmountLocal);
        Assert.Equal(5.5m, result.CommissionPercentage);
    }

    [Fact]
    public async Task ProcessCashAdvance_Cash_MissingCommissionRejectsWithoutPayoutOrSale()
    {
        using var context = GetInMemoryDbContext();

        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync((string?)null);

        var coordinator = CreateCoordinatorWithSettings(context, settingsMock.Object);
        var drawerService = new ServerCashService.CashDrawerService(context);
        var session = await drawerService.OpenSessionAsync(2000m, 50.0m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ProcessAsync(
                sessionId: session.Id,
                requestedAmountLocal: 1000m,
                paymentMethodId: 2,
                paymentMethodName: "Punto de Venta",
                isTransfer: false,
                exchangeRate: 50.0m
            ));

        Assert.Contains("no está configurada", ex.Message);

        var balance = await drawerService.GetCurrentBalanceLocalAsync(session.Id);
        Assert.Equal(2000m, balance);

        var saleCount = await context.Sales.CountAsync();
        Assert.Equal(0, saleCount);
    }

    [Fact]
    public async Task ProcessCashAdvance_InsufficientCash_ThrowsInvalidOperationException()
    {
        using var context = GetInMemoryDbContext();
        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct))
            .ReturnsAsync("7.0");
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceCashCommissionPct))
            .ReturnsAsync("10.0");
        var coordinator = CreateCoordinatorWithSettings(context, settingsMock.Object);
        var drawerService = new ServerCashService.CashDrawerService(context);

        var session = await drawerService.OpenSessionAsync(500m, 50.0m);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await coordinator.ProcessAsync(
                sessionId: session.Id,
                requestedAmountLocal: 1000m,
                paymentMethodId: 2,
                paymentMethodName: "Transferencia",
                isTransfer: true,
                exchangeRate: 50.0m
            );
        });
    }

    private static Mock<ClientCashService.ICashDrawerService> CreateAdvanceDrawerMock(decimal transferPercentage, decimal cashPercentage)
    {
        var drawer = new Mock<ClientCashService.ICashDrawerService>();
        drawer.Setup(d => d.GetAdvanceCommissionAsync(true, It.IsAny<CancellationToken>())).ReturnsAsync(transferPercentage);
        drawer.Setup(d => d.GetAdvanceCommissionAsync(false, It.IsAny<CancellationToken>())).ReturnsAsync(cashPercentage);
        return drawer;
    }

    private static List<ClientCashService.PaymentMethodDto> CreateElectronicMethods() => new()
    {
        new ClientCashService.PaymentMethodDto { Id = 1, Name = "Efectivo", IsCash = true, DisplayOrder = 1 },
        new ClientCashService.PaymentMethodDto { Id = 2, Name = "Transferencia", IsCash = false, DisplayOrder = 2 },
        new ClientCashService.PaymentMethodDto { Id = 3, Name = "Punto de Venta", IsCash = false, DisplayOrder = 3 }
    };

    [Fact]
    public async Task CashAdvanceRegisterViewModel_PreviewShowsServerResolvedPercentage()
    {
        var vm = new CashAdvanceRegisterViewModel(
            CreateElectronicMethods(),
            availableCashLocal: 2000m,
            exchangeRate: 50.0m,
            cashDrawer: CreateAdvanceDrawerMock(5.5m, 10.0m).Object);

        await vm.RefreshCommissionAsync();

        Assert.DoesNotContain(vm.ElectronicPaymentMethods, pm => pm.IsCash || pm.Name.Equals("Efectivo", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, vm.ElectronicPaymentMethods.Count);
        Assert.Equal("Transferencia", vm.SelectedPaymentMethod?.Name);

        vm.RequestedAmountBsS = 1000m;
        Assert.True(vm.IsTransfer);
        Assert.Equal(5.5m, vm.CommissionPercentage);
        Assert.Equal(55m, vm.CommissionAmountBsS);
        Assert.Equal(1055m, vm.TotalToChargeBsS);
        Assert.True(vm.CanConfirm);
    }

    [Fact]
    public async Task CashAdvanceRegisterViewModel_PreviewFollowsTheSelectedChannel()
    {
        var vm = new CashAdvanceRegisterViewModel(
            CreateElectronicMethods(),
            availableCashLocal: 2000m,
            exchangeRate: 50.0m,
            cashDrawer: CreateAdvanceDrawerMock(5.5m, 12.25m).Object);

        await vm.RefreshCommissionAsync();
        vm.RequestedAmountBsS = 1000m;
        Assert.True(vm.IsTransfer);
        Assert.Equal(5.5m, vm.CommissionPercentage);

        vm.SelectedPaymentMethod = vm.ElectronicPaymentMethods.First(pm => pm.Name == "Punto de Venta");
        await vm.RefreshCommissionAsync();

        Assert.False(vm.IsTransfer);
        Assert.Equal(12.25m, vm.CommissionPercentage);
        Assert.Equal(122.50m, vm.CommissionAmountBsS);
    }

    [Fact]
    public async Task CashAdvanceRegisterViewModel_WhenCommissionUnresolved_BlocksConfirmation()
    {
        var drawer = new Mock<ClientCashService.ICashDrawerService>();
        drawer.Setup(d => d.GetAdvanceCommissionAsync(It.IsAny<bool>(), It.IsAny<CancellationToken>())).ReturnsAsync((decimal?)null);

        var vm = new CashAdvanceRegisterViewModel(
            CreateElectronicMethods(),
            availableCashLocal: 2000m,
            exchangeRate: 50.0m,
            cashDrawer: drawer.Object);

        await vm.RefreshCommissionAsync();
        vm.RequestedAmountBsS = 1000m;

        Assert.Null(vm.CommissionPercentage);
        Assert.Null(vm.CommissionAmountBsS);
        Assert.Null(vm.TotalToChargeBsS);
        Assert.False(vm.CanConfirm);
    }

    [Fact]
    public async Task CreateCashAdvanceSale_WhenCashierAndInfrastructureProductMissing_ProvisionsProductWithoutCatalogPermission()
    {
        using var salesContext = GetInMemoryDbContext();
        using var inventoryContext = GetInMemoryInventoryDbContext();

        var cashier = new MockCurrentUserService { UserRole = UserRole.Cashier };
        var inventoryService = new InventoryService(inventoryContext, cashier);

        var salesService = new SalesService(
            salesContext,
            inventoryService,
            new Mock<IMediator>().Object,
            new Mock<ICashDrawerService>().Object,
            new Mock<ISystemSettingsService>().Object);

        var sale = await salesService.CreateCashAdvanceSaleAsync(
            requestedAmountLocal: 1000m,
            commissionAmountLocal: 70m,
            paymentMethodId: 2,
            paymentMethodName: "Transferencia",
            isTransfer: true,
            exchangeRate: 50m);

        Assert.NotNull(sale);

        var provisioned = Assert.Single(inventoryContext.Products.Where(p => p.SKU == "ADV-001"));
        Assert.True(provisioned.IsCashAdvance);
        Assert.Equal(0m, provisioned.StockQuantity);

        var saleItem = await salesContext.SaleItems.FirstAsync(si => si.SaleId == sale.Id);
        Assert.Equal(provisioned.Id, saleItem.ProductId);

        // System provisioning must not grant the cashier catalog mutation rights.
        var directEx = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => inventoryService.CreateProductFromDtoAsync(
            new CreateProductDto { Name = "Manual", SKU = "MAN-001", CostPriceUSD = 1m, PriceRetailUSD = 2m }));
        Assert.Contains("no tiene permisos para modificar el catálogo", directEx.Message);
    }

    [Fact]
    public async Task CreateSystemProduct_CashAdvanceWithSentinelStock_ForcesZeroStockAndStaysSilentInStockMovements()
    {
        using var inventoryContext = GetInMemoryInventoryDbContext();
        var cashier = new MockCurrentUserService { UserRole = UserRole.Cashier, UserId = "cashier-7" };
        var inventoryService = new InventoryService(inventoryContext, cashier);

        var id = await inventoryService.CreateSystemProductAsync(new CreateSystemProductRequest
        {
            Name = "Adelanto de Efectivo",
            SKU = "ADV-001",
            PriceRetailUSD = 0m,
            StockQuantity = 999999,
            IsCashAdvance = true,
            IsActive = true
        });

        var product = await inventoryContext.Products.FindAsync(id);
        Assert.NotNull(product);
        Assert.Equal(0m, product.StockQuantity);
        Assert.Empty(await inventoryContext.StockMovements.ToListAsync());
    }
}
