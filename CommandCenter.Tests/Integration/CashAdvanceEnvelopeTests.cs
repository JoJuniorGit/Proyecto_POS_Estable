using System;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// Envelope atómico del adelanto de efectivo (venta + movimientos de gaveta) contra PostgreSQL real
/// en base AISLADA por test (carril <see cref="PostgresTestLane"/>). La versión anterior tomaba la
/// base COMPARTIDA del harness legacy (TestSchemaBootstrap): una sesión de caja residual de otra
/// corrida o el estado sucio de la base degradaban el open y las aserciones de conteo. Sin conexión
/// utilizable el carril falla EXPLÍCITO con la instrucción de configuración (nunca silent-pass).
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(PostgresRealCollection.Name)]
public class CashAdvanceEnvelopeTests
{
    private static Mock<ISystemSettingsService> CreateSettingsMock()
    {
        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct))
            .ReturnsAsync("7.0");
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceCashCommissionPct))
            .ReturnsAsync("10.0");
        return settingsMock;
    }

    private static (CashAdvanceCoordinator coordinator, ICashDrawerService drawerService) CreateCoordinator(SalesDbContext context)
    {
        var inventoryMock = new Mock<IInventoryService>();
        inventoryMock.Setup(i => i.GetCashAdvanceProductAsync())
            .ReturnsAsync(new SaleProductInfoDto { Id = 1, Name = "Adelanto de Efectivo", IsCashAdvance = true });

        var cashDrawerMock = new Mock<ICashDrawerService>();
        cashDrawerMock.Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        var mediatorMock = new Mock<IMediator>();
        var settingsMock = CreateSettingsMock();

        var salesService = new SalesService(context, inventoryMock.Object, mediatorMock.Object, cashDrawerMock.Object, settingsMock.Object);
        var drawerService = new CashDrawerService(context);

        return (new CashAdvanceCoordinator(context, salesService, drawerService, settingsMock.Object), drawerService);
    }

    [Fact]
    public async Task Envelope_CommitsBothSaleAndDrawerMovementsAtomically()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(seedStandardSalesData: true);
        await using var context = database.CreateSalesDbContext();

        var (coordinator, drawerService) = CreateCoordinator(context);
        var session = await drawerService.OpenSessionAsync(5000m, 50.0m);

        var result = await coordinator.ProcessAsync(
            sessionId: session.Id,
            requestedAmountLocal: 1000m,
            paymentMethodId: 3,
            paymentMethodName: "Punto de Venta",
            isTransfer: true,
            exchangeRate: 50.0m
        );

        Assert.NotNull(result.RelatedSaleId);

        await using var verifyContext = database.CreateSalesDbContext();
        var sale = await verifyContext.Sales.FindAsync(result.RelatedSaleId!.Value);
        Assert.NotNull(sale);
        Assert.Equal(SaleStatus.Completed, sale.Status);

        var drawerTxs = await verifyContext.CashTransactions
            .CountAsync(t => t.SessionId == session.Id && t.Source == CashTransactionSource.CashAdvance);
        Assert.Equal(2, drawerTxs);
    }

    [Fact]
    public async Task Envelope_SaleFailure_NoDrawerTransactionPersists()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(seedStandardSalesData: true);
        await using var context = database.CreateSalesDbContext();

        var settingsMock = CreateSettingsMock();

        var inventoryMock = new Mock<IInventoryService>();
        inventoryMock.Setup(i => i.GetCashAdvanceProductAsync())
            .ThrowsAsync(new InvalidOperationException("Simulated DB failure"));

        var drawerService = new CashDrawerService(context);
        var session = await drawerService.OpenSessionAsync(5000m, 50.0m);

        var mediatorMock = new Mock<IMediator>();
        var cashDrawerMock = new Mock<ICashDrawerService>();
        var salesService = new SalesService(context, inventoryMock.Object, mediatorMock.Object, cashDrawerMock.Object, settingsMock.Object);
        var coordinator = new CashAdvanceCoordinator(context, salesService, drawerService, settingsMock.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ProcessAsync(
                sessionId: session.Id,
                requestedAmountLocal: 1000m,
                paymentMethodId: 3,
                paymentMethodName: "Punto de Venta",
                isTransfer: true,
                exchangeRate: 50.0m
            ));

        await using var verifyContext = database.CreateSalesDbContext();
        var drawerTxs = await verifyContext.CashTransactions
            .CountAsync(t => t.SessionId == session.Id && t.Source == CashTransactionSource.CashAdvance);
        Assert.Equal(0, drawerTxs);
    }

    [Fact]
    public async Task Envelope_DrawerFailure_RollsBackSaleAndTransactions()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(seedStandardSalesData: true);
        await using var context = database.CreateSalesDbContext();

        var inventoryMock = new Mock<IInventoryService>();
        inventoryMock.Setup(i => i.GetCashAdvanceProductAsync())
            .ReturnsAsync(new SaleProductInfoDto { Id = 1, Name = "Adelanto de Efectivo", IsCashAdvance = true });

        var settingsMock = CreateSettingsMock();

        var mediatorMock = new Mock<IMediator>();
        var salesService = new SalesService(context, inventoryMock.Object, mediatorMock.Object,
            Mock.Of<ICashDrawerService>(), settingsMock.Object);

        var fakeDrawer = new Mock<ICashDrawerService>();
        fakeDrawer.Setup(c => c.GetCurrentBalanceLocalAsync(It.IsAny<int>()))
            .ReturnsAsync(5000m);
        fakeDrawer.Setup(c => c.AddTransactionAsync(
            It.IsAny<int>(), It.IsAny<CashTransactionType>(), It.IsAny<CashTransactionSource>(),
            It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>(),
            It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<int?>()))
            .ThrowsAsync(new InvalidOperationException("Simulated drawer failure"));

        var coordinator = new CashAdvanceCoordinator(context, salesService, fakeDrawer.Object, settingsMock.Object);

        var realDrawer = new CashDrawerService(context);
        var session = await realDrawer.OpenSessionAsync(5000m, 50.0m);

        var saleCountBefore = await context.Sales.CountAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ProcessAsync(
                sessionId: session.Id,
                requestedAmountLocal: 1000m,
                paymentMethodId: 3,
                paymentMethodName: "Punto de Venta",
                isTransfer: true,
                exchangeRate: 50.0m
            ));

        var saleCountAfter = await context.Sales.CountAsync();
        Assert.Equal(saleCountBefore, saleCountAfter);
    }
}
