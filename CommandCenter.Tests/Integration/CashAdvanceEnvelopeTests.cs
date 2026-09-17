using System;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services;
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

[Collection(PostgresRealCollection.Name)]
public class CashAdvanceEnvelopeTests : IDisposable
{
    private readonly SalesDbContext? _context;

    public CashAdvanceEnvelopeTests()
    {
        _context = TestDatabaseFactory.CreatePostgreSqlSalesDbContext();
    }

    public void Dispose()
    {
        _context?.Dispose();
    }

    private bool IsPostgresAvailable => _context != null;

    private void SkipIfNoPostgres()
    {
        if (IsPostgresAvailable) return;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("GITHUB_ACTIONS")))
        {
            throw new InvalidOperationException(
                "TEST_POSTGRES_CONNECTION no está definida en CI. Configure PostgreSQL real para ejecutar CashAdvanceEnvelopeTests.");
        }
    }

    private (CashAdvanceCoordinator coordinator, ICashDrawerService drawerService) CreateCoordinator()
    {
        var inventoryMock = new Mock<IInventoryService>();
        inventoryMock.Setup(i => i.GetCashAdvanceProductAsync())
            .ReturnsAsync(new Product { Id = 1, Name = "Adelanto de Efectivo", IsCashAdvance = true });

        var cashDrawerMock = new Mock<ICashDrawerService>();
        cashDrawerMock.Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        var mediatorMock = new Mock<IMediator>();
        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct))
            .ReturnsAsync("7.0");
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceCashCommissionPct))
            .ReturnsAsync("10.0");

        var salesService = new SalesService(_context!, inventoryMock.Object, mediatorMock.Object, cashDrawerMock.Object, settingsMock.Object);
        var drawerService = new CashDrawerService(_context!);

        return (new CashAdvanceCoordinator(_context!, salesService, drawerService, settingsMock.Object), drawerService);
    }

    [Fact]
    public async Task Envelope_CommitsBothSaleAndDrawerMovementsAtomically()
    {
        SkipIfNoPostgres();
        if (!IsPostgresAvailable) return;

        await TestDatabaseFactory.SeedStandardSalesDataAsync(_context!);
        var (coordinator, drawerService) = CreateCoordinator();
        var session = await drawerService.OpenSessionAsync(5000m, 50.0m);

        var result = await coordinator.ProcessAsync(
            sessionId: session.Id,
            requestedAmountLocal: 1000m,
            paymentMethodId: 2,
            paymentMethodName: "Transferencia Bancaria",
            isTransfer: true,
            exchangeRate: 50.0m
        );

        Assert.NotNull(result.RelatedSaleId);

        var sale = await _context!.Sales.FindAsync(result.RelatedSaleId!.Value);
        Assert.NotNull(sale);
        Assert.Equal(SaleStatus.Completed, sale.Status);

        var drawerTxs = await _context!.CashTransactions
            .CountAsync(t => t.SessionId == session.Id && t.Source == CashTransactionSource.CashAdvance);
        Assert.Equal(2, drawerTxs);
    }

    [Fact]
    public async Task Envelope_SaleFailure_NoDrawerTransactionPersists()
    {
        SkipIfNoPostgres();
        if (!IsPostgresAvailable) return;

        await TestDatabaseFactory.SeedStandardSalesDataAsync(_context!);

        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct))
            .ReturnsAsync("7.0");
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceCashCommissionPct))
            .ReturnsAsync("10.0");

        var inventoryMock = new Mock<IInventoryService>();
        inventoryMock.Setup(i => i.GetCashAdvanceProductAsync())
            .ThrowsAsync(new InvalidOperationException("Simulated DB failure"));

        var drawerService = new CashDrawerService(_context!);
        var session = await drawerService.OpenSessionAsync(5000m, 50.0m);

        var mediatorMock = new Mock<IMediator>();
        var cashDrawerMock = new Mock<ICashDrawerService>();
        var salesService = new SalesService(_context!, inventoryMock.Object, mediatorMock.Object, cashDrawerMock.Object, settingsMock.Object);
        var coordinator = new CashAdvanceCoordinator(_context!, salesService, drawerService, settingsMock.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ProcessAsync(
                sessionId: session.Id,
                requestedAmountLocal: 1000m,
                paymentMethodId: 2,
                paymentMethodName: "Transferencia Bancaria",
                isTransfer: true,
                exchangeRate: 50.0m
            ));

        var drawerTxs = await _context!.CashTransactions
            .CountAsync(t => t.SessionId == session.Id && t.Source == CashTransactionSource.CashAdvance);
        Assert.Equal(0, drawerTxs);
    }

    [Fact]
    public async Task Envelope_DrawerFailure_RollsBackSaleAndTransactions()
    {
        SkipIfNoPostgres();
        if (!IsPostgresAvailable) return;

        await TestDatabaseFactory.SeedStandardSalesDataAsync(_context!);

        var inventoryMock = new Mock<IInventoryService>();
        inventoryMock.Setup(i => i.GetCashAdvanceProductAsync())
            .ReturnsAsync(new Product { Id = 1, Name = "Adelanto de Efectivo", IsCashAdvance = true });

        var settingsMock = new Mock<ISystemSettingsService>();
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct))
            .ReturnsAsync("7.0");
        settingsMock.Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceCashCommissionPct))
            .ReturnsAsync("10.0");

        var mediatorMock = new Mock<IMediator>();
        var salesService = new SalesService(_context!, inventoryMock.Object, mediatorMock.Object,
            Mock.Of<ICashDrawerService>(), settingsMock.Object);

        var fakeDrawer = new Mock<ICashDrawerService>();
        fakeDrawer.Setup(c => c.GetCurrentBalanceLocalAsync(It.IsAny<int>()))
            .ReturnsAsync(5000m);
        fakeDrawer.Setup(c => c.AddTransactionAsync(
            It.IsAny<int>(), It.IsAny<CashTransactionType>(), It.IsAny<CashTransactionSource>(),
            It.IsAny<decimal>(), It.IsAny<decimal>(), It.IsAny<decimal>(),
            It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<bool>(), It.IsAny<int?>()))
            .ThrowsAsync(new InvalidOperationException("Simulated drawer failure"));

        var coordinator = new CashAdvanceCoordinator(_context!, salesService, fakeDrawer.Object, settingsMock.Object);

        var realDrawer = new CashDrawerService(_context!);
        var session = await realDrawer.OpenSessionAsync(5000m, 50.0m);

        var saleCountBefore = await _context!.Sales.CountAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            coordinator.ProcessAsync(
                sessionId: session.Id,
                requestedAmountLocal: 1000m,
                paymentMethodId: 2,
                paymentMethodName: "Transferencia Bancaria",
                isTransfer: true,
                exchangeRate: 50.0m
            ));

        var saleCountAfter = await _context!.Sales.CountAsync();
        Assert.Equal(saleCountBefore, saleCountAfter);
    }
}
