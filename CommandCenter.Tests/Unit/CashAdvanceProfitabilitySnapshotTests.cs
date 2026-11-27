using System;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Xunit;
using CashDrawerService = Sales.Module.Services.CashDrawerService;
using CashDrawerStatus = Sales.Module.Entities.CashDrawerStatus;
using SalesService = Sales.Module.Services.SalesService;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Paso 14 (F1-WU2, S2/S3/S10): wiring de servicio de la base de rentabilidad. El avance de
/// efectivo persiste su naturaleza y la comision de servicio congelada, el capital no se trata
/// como venta de mercancia y la anulacion deja su timestamp real para las reversas del periodo.
/// </summary>
public class CashAdvanceProfitabilitySnapshotTests
{
    private static (Sales.Module.Services.CashAdvanceCoordinator Coordinator, CashDrawerService Drawer, SalesDbContext Context) CreateCashAdvanceHarness(
        decimal transferCommission = 7m,
        decimal cashCommission = 10m)
    {
        var context = TestDatabaseFactory.CreateSalesDbContext();
        var settings = new Mock<ISystemSettingsService>();
        settings
            .Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceTransferCommissionPct))
            .ReturnsAsync(transferCommission.ToString(System.Globalization.CultureInfo.InvariantCulture));
        settings
            .Setup(s => s.GetSettingAsync(Core.Constants.SettingKeys.CashAdvanceCashCommissionPct))
            .ReturnsAsync(cashCommission.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var inventory = new Mock<IInventoryService>();
        inventory
            .Setup(i => i.GetCashAdvanceProductAsync(It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new SaleProductInfoDto { Id = 1, Name = "Adelanto de Efectivo", IsCashAdvance = true });

        var cashDrawerMock = new Mock<ICashDrawerService>();
        cashDrawerMock
            .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>(), It.IsAny<System.Threading.CancellationToken>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        var salesService = new SalesService(
            context,
            inventory.Object,
            new Mock<IMediator>().Object,
            cashDrawerMock.Object,
            settings.Object);
        var drawer = new CashDrawerService(context);
        var coordinator = new Sales.Module.Services.CashAdvanceCoordinator(
            context,
            salesService,
            drawer,
            settings.Object);

        return (coordinator, drawer, context);
    }

    [Fact]
    public async Task ProcessCashAdvance_PersistsSaleTypeAndCommissionSnapshot()
    {
        var (coordinator, drawer, context) = CreateCashAdvanceHarness();
        using (context)
        {
            var session = await drawer.OpenSessionAsync(2000m, 50.0m);

            var result = await coordinator.ProcessAsync(
                sessionId: session.Id,
                requestedAmountLocal: 1000m,
                paymentMethodId: 2,
                paymentMethodName: "Transferencia Bancaria",
                isTransfer: true,
                exchangeRate: 50.0m);

            Assert.NotNull(result.RelatedSaleId);
            var sale = context.Sales.Single(s => s.Id == result.RelatedSaleId!.Value);

            Assert.Equal(SaleType.CashAdvance, sale.SaleType);
            Assert.Equal(70m, sale.CommissionAmountLocal);
            Assert.Equal(1.4m, sale.CommissionAmountUSD);

            var saleItem = Assert.Single(context.SaleItems.Where(item => item.SaleId == sale.Id));
            Assert.Equal(0m, saleItem.UnitCostLocal);

            var advanceTransactions = context.CashTransactions
                .Where(transaction => transaction.Source == CashTransactionSource.CashAdvance
                    && transaction.SaleId == sale.Id)
                .ToList();
            Assert.Equal(2, advanceTransactions.Count);
            Assert.Contains(
                advanceTransactions,
                transaction => transaction.Type == CashTransactionType.Expense && transaction.IsPhysicalCash);
            Assert.Contains(
                advanceTransactions,
                transaction => transaction.Type == CashTransactionType.Income && !transaction.IsPhysicalCash);
        }
    }

    [Fact]
    public async Task CancelSaleAsync_SetsCancelledAtUtc()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var sale = new Sale
        {
            Status = SaleStatus.OnHold,
            DeliveryStatus = SaleDeliveryStatus.PendingPickup,
            ClaimedByUserId = 7,
            ClaimedByUserName = "Cajero",
            ClaimAction = SaleClaimAction.Editing,
            ClaimedAtUtc = DateTime.UtcNow
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var service = new SalesService(
            context,
            new Mock<IInventoryService>().Object,
            new Mock<IMediator>().Object,
            new Mock<ICashDrawerService>().Object,
            new Mock<ISystemSettingsService>().Object);

        var before = DateTime.UtcNow;
        await service.CancelSaleAsync(sale.Id, actingUserId: 7);
        var after = DateTime.UtcNow;

        context.ChangeTracker.Clear();
        var persisted = await context.Sales.AsNoTracking().FirstAsync(candidate => candidate.Id == sale.Id);
        Assert.Equal(SaleStatus.Cancelled, persisted.Status);
        Assert.NotNull(persisted.CancelledAtUtc);
        Assert.InRange(persisted.CancelledAtUtc!.Value, before.AddSeconds(-5), after.AddSeconds(5));
    }
}
