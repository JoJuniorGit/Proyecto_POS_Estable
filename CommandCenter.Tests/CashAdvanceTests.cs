using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ClientCashService = Desktop.Client.Services;
using Desktop.Client.ViewModels;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;
using ServerCashService = Sales.Module.Services;
using Xunit;
using Core.DTOs;

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

    [Fact]
    public async Task ProcessCashAdvance_Transfer_Applies7PercentCommission_And_DeductsOnlyRequestedFromPhysicalDrawer()
    {
        using var context = GetInMemoryDbContext();
        var service = new ServerCashService.CashDrawerService(context);

        // Open session with 2,000 Bs.S opening balance
        var session = await service.OpenSessionAsync(2000m, 50.0m);

        // Process Cash Advance for 1,000 Bs.S via Transfer
        var result = await service.ProcessCashAdvanceAsync(
            sessionId: session.Id,
            requestedAmountLocal: 1000m,
            paymentMethodId: 2,
            paymentMethodName: "Transferencia Bancaria",
            isTransfer: true,
            exchangeRate: 50.0m
        );

        Assert.Equal(1000m, result.RequestedAmountLocal);
        Assert.Equal(70m, result.CommissionAmountLocal); // 7% of 1,000
        Assert.Equal(1070m, result.TotalChargedLocal);
        Assert.Equal(7.0m, result.CommissionPercentage);

        // Verify Expense (physical cash out) and Income (non-physical commission)
        Assert.True(result.ExpenseTransaction.IsPhysicalCash);
        Assert.Equal(CashTransactionType.Expense, result.ExpenseTransaction.Type);
        Assert.Equal(1000m, result.ExpenseTransaction.AmountLocal);

        Assert.False(result.IncomeTransaction.IsPhysicalCash);
        Assert.Equal(CashTransactionType.Income, result.IncomeTransaction.Type);
        Assert.Equal(70m, result.IncomeTransaction.AmountLocal);

        // Verify Physical Drawer Balance: Opening (2,000) - Expense (1,000) = 1,000 (Commission 70 is excluded from physical cash balance)
        var physicalBalance = await service.GetCurrentBalanceLocalAsync(session.Id);
        Assert.Equal(1000m, physicalBalance);
    }

    [Fact]
    public async Task ProcessCashAdvance_GeneratesCompletedSale_WithConsecutiveInvoiceNumber()
    {
        using var context = GetInMemoryDbContext();
        var service = new ServerCashService.CashDrawerService(context);

        var session = await service.OpenSessionAsync(2000m, 50.0m);

        var result = await service.ProcessCashAdvanceAsync(
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
        Assert.Equal(21.4m, sale.TotalUSD); // 1070 / 50 = 21.4 USD
        Assert.Single(sale.Items);
        Assert.Single(sale.Payments);
    }

    [Fact]
    public async Task ProcessCashAdvance_DoesNotCreateAdditionalCashTransactions()
    {
        using var context = GetInMemoryDbContext();
        var service = new ServerCashService.CashDrawerService(context);

        var session = await service.OpenSessionAsync(2000m, 50.0m);

        await service.ProcessCashAdvanceAsync(
            sessionId: session.Id,
            requestedAmountLocal: 1000m,
            paymentMethodId: 2,
            paymentMethodName: "Transferencia",
            isTransfer: true,
            exchangeRate: 50.0m
        );

        // Session transactions: 1 Opening + 1 Expense + 1 Commission Income = exactly 3 total transactions
        var sessionTxCount = await context.CashTransactions.CountAsync(t => t.SessionId == session.Id);
        Assert.Equal(3, sessionTxCount);

        // CashAdvance specific transactions: exactly 2 (1 Expense, 1 Income)
        var cashAdvanceTxCount = await context.CashTransactions.CountAsync(t => t.Source == CashTransactionSource.CashAdvance);
        Assert.Equal(2, cashAdvanceTxCount);
    }

    [Fact]
    public async Task ProcessCashAdvance_SaleIntegration_DoesNotAffectDrawerBalanceTwice()
    {
        using var context = GetInMemoryDbContext();
        var service = new ServerCashService.CashDrawerService(context);

        var session = await service.OpenSessionAsync(2000m, 50.0m);

        await service.ProcessCashAdvanceAsync(
            sessionId: session.Id,
            requestedAmountLocal: 1000m,
            paymentMethodId: 2,
            paymentMethodName: "Transferencia",
            isTransfer: true,
            exchangeRate: 50.0m
        );

        // Physical Balance: Opening (2,000) - Requested Expense (1,000) = 1,000 Bs.S
        var physicalBalance = await service.GetCurrentBalanceLocalAsync(session.Id);
        Assert.Equal(1000m, physicalBalance);
    }

    [Fact]
    public async Task ProcessCashAdvance_SaleItem_HasExplicitPriceOverridingProductDefault()
    {
        using var context = GetInMemoryDbContext();
        var service = new ServerCashService.CashDrawerService(context);

        var session = await service.OpenSessionAsync(2000m, 50.0m);

        var result = await service.ProcessCashAdvanceAsync(
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
        var service = new ServerCashService.CashDrawerService(context);

        var session = await service.OpenSessionAsync(2000m, 50.0m);

        // Process Cash Advance for 1,000 Bs.S via Punto de Venta (non-transfer electronic)
        var result = await service.ProcessCashAdvanceAsync(
            sessionId: session.Id,
            requestedAmountLocal: 1000m,
            paymentMethodId: 3,
            paymentMethodName: "Punto de Venta",
            isTransfer: false,
            exchangeRate: 50.0m
        );

        Assert.Equal(1000m, result.RequestedAmountLocal);
        Assert.Equal(100m, result.CommissionAmountLocal); // 10% of 1,000
        Assert.Equal(1100m, result.TotalChargedLocal);
        Assert.Equal(10.0m, result.CommissionPercentage);
    }

    [Fact]
    public async Task ProcessCashAdvance_InsufficientCash_ThrowsInvalidOperationException()
    {
        using var context = GetInMemoryDbContext();
        var service = new ServerCashService.CashDrawerService(context);

        var session = await service.OpenSessionAsync(500m, 50.0m);

        // Attempting to withdraw 1,000 Bs.S when physical balance is only 500 Bs.S
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await service.ProcessCashAdvanceAsync(
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

        // Physical Cash must be filtered out
        Assert.DoesNotContain(vm.ElectronicPaymentMethods, pm => pm.IsCash || pm.Name.Equals("Efectivo", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2, vm.ElectronicPaymentMethods.Count);
        Assert.Equal("Transferencia", vm.SelectedPaymentMethod?.Name);

        // 1,000 Bs.S requested -> 7% transfer commission = 70 Bs.S, Total = 1,070 Bs.S
        vm.RequestedAmountBsS = 1000m;
        Assert.True(vm.IsTransfer);
        Assert.Equal(7.0m, vm.CommissionPercentage);
        Assert.Equal(70m, vm.CommissionAmountBsS);
        Assert.Equal(1070m, vm.TotalToChargeBsS);
        Assert.True(vm.CanConfirm);
    }
}
