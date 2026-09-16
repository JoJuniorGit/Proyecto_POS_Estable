using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClientCashService = Desktop.Client.Services;
using Microsoft.Extensions.DependencyInjection;
using Core.Entities;
using Core.Interfaces;
using Desktop.Client.ViewModels;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
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

    private ServerCashService.CashAdvanceCoordinator CreateCoordinatorWithSalesService(SalesDbContext context, ISystemSettingsService? settingsService = null)
    {
        var inventoryMock = new Mock<IInventoryService>();
        inventoryMock.Setup(i => i.GetCashAdvanceProductAsync())
            .ReturnsAsync(new Product { Id = 1, Name = "Adelanto de Efectivo", IsCashAdvance = true });

        var cashDrawerMock = new Mock<ICashDrawerService>();
        cashDrawerMock.Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSession { Id = 1, Status = CashDrawerStatus.Open });

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
            .ReturnsAsync(new Product { Id = 1, Name = "Adelanto de Efectivo", IsCashAdvance = true });

        var cashDrawerMock = new Mock<ICashDrawerService>();
        cashDrawerMock.Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSession { Id = 1, Status = CashDrawerStatus.Open });

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

    [Fact]
    public void CashAdvanceRegisterViewModel_FiltersOutCashPaymentMethods()
    {
        var methods = new List<ClientCashService.PaymentMethodDto>
        {
            new ClientCashService.PaymentMethodDto { Id = 1, Name = "Efectivo", IsCash = true, DisplayOrder = 1 },
            new ClientCashService.PaymentMethodDto { Id = 2, Name = "Transferencia", IsCash = false, DisplayOrder = 2 },
            new ClientCashService.PaymentMethodDto { Id = 3, Name = "Punto de Venta", IsCash = false, DisplayOrder = 3 }
        };

        var vm = new CashAdvanceRegisterViewModel(methods, availableCashLocal: 2000m, exchangeRate: 50.0m);

        Assert.DoesNotContain(vm.ElectronicPaymentMethods, pm => pm.IsCash || pm.Name.Equals("Efectivo", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, vm.ElectronicPaymentMethods.Count);
        Assert.Equal("Transferencia", vm.SelectedPaymentMethod?.Name);

        vm.RequestedAmountBsS = 1000m;
        Assert.True(vm.IsTransfer);
        Assert.Equal(7.0m, vm.CommissionPercentage);
        Assert.Equal(70m, vm.CommissionAmountBsS);
        Assert.Equal(1070m, vm.TotalToChargeBsS);
        Assert.True(vm.CanConfirm);
    }
}
