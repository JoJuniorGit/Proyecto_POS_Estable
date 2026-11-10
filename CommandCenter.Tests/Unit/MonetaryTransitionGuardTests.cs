using System;
using System.Threading.Tasks;
using Backend.API.Services;
using Core.Entities;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;
using Xunit;

namespace CommandCenter.Tests.Unit;

// 8.211-T3c3a (S11): the transition guard only reports rows whose monetary interpretation would
// be silently rewritten by a base change. Configuration, catalog and rate rows are excluded.
public class MonetaryTransitionGuardTests
{
    private static InventoryDbContext CreateInventoryDb()
        => new(new DbContextOptionsBuilder<InventoryDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options);

    private static SalesDbContext CreateSalesDb()
        => new(new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task HasFinancialData_WhenBothContextsAreEmpty_ReturnsFalse()
    {
        using var inventory = CreateInventoryDb();
        using var sales = CreateSalesDb();
        var guard = new MonetaryTransitionGuard(inventory, sales);

        Assert.False(await guard.HasFinancialDataAsync());
    }

    public enum FinancialRow
    {
        Product,
        SupplierInvoice,
        Sale,
        SalePayment,
        CashDrawerSession,
        DailyClosure,
        Receivable,
        PettyCashExpense
    }

    [Theory]
    [InlineData(FinancialRow.Product)]
    [InlineData(FinancialRow.SupplierInvoice)]
    [InlineData(FinancialRow.Sale)]
    [InlineData(FinancialRow.SalePayment)]
    [InlineData(FinancialRow.CashDrawerSession)]
    [InlineData(FinancialRow.DailyClosure)]
    [InlineData(FinancialRow.Receivable)]
    [InlineData(FinancialRow.PettyCashExpense)]
    public async Task HasFinancialData_WithAnyFinancialRow_ReturnsTrue(FinancialRow row)
    {
        using var inventory = CreateInventoryDb();
        using var sales = CreateSalesDb();

        await SeedFinancialRowAsync(row, inventory, sales);
        var guard = new MonetaryTransitionGuard(inventory, sales);

        Assert.True(await guard.HasFinancialDataAsync());
    }

    [Fact]
    public async Task HasFinancialData_WithOnlySeededSystemCashAdvanceProduct_ReturnsFalse()
    {
        using var inventory = CreateInventoryDb();
        using var sales = CreateSalesDb();

        // Production startup seeds this infrastructure product before serving requests.
        inventory.Products.Add(new Product
        {
            Name = "Adelanto de Efectivo",
            SKU = "ADV-001",
            IsCashAdvance = true
        });
        await inventory.SaveChangesAsync();

        var guard = new MonetaryTransitionGuard(inventory, sales);

        Assert.False(await guard.HasFinancialDataAsync());
    }

    [Fact]
    public async Task HasFinancialData_WithOnlySupportingRows_ReturnsFalse()
    {
        using var inventory = CreateInventoryDb();
        using var sales = CreateSalesDb();

        inventory.ExchangeRateHistory.Add(new ExchangeRateHistory
        {
            Date = new DateOnly(2026, 10, 28),
            Rate = 36.5m
        });
        inventory.SystemSettings.Add(new SystemSetting
        {
            Key = "Monetary.Mode",
            Value = "Dual"
        });
        sales.Users.Add(new User
        {
            Id = 500,
            Cedula = "99999999",
            Name = "Support Tester",
            Username = "support-tester",
            Role = UserRole.Admin
        });
        sales.PaymentMethods.Add(new PaymentMethod
        {
            Id = 500,
            Name = "Support Tester Method"
        });
        await inventory.SaveChangesAsync();
        await sales.SaveChangesAsync();

        var guard = new MonetaryTransitionGuard(inventory, sales);

        Assert.False(await guard.HasFinancialDataAsync());
    }

    private static async Task SeedFinancialRowAsync(
        FinancialRow row,
        InventoryDbContext inventory,
        SalesDbContext sales)
    {
        switch (row)
        {
            case FinancialRow.Product:
                inventory.Products.Add(new Product { Name = "Guarded product", SKU = "GUARD-1" });
                await inventory.SaveChangesAsync();
                break;

            case FinancialRow.SupplierInvoice:
                inventory.SupplierInvoices.Add(new SupplierInvoice { SupplierId = 1 });
                await inventory.SaveChangesAsync();
                break;

            case FinancialRow.Sale:
                sales.Sales.Add(new Sale { Status = SaleStatus.Pending });
                await sales.SaveChangesAsync();
                break;

            case FinancialRow.SalePayment:
                sales.SalePayments.Add(new SalePayment
                {
                    SaleId = 1,
                    PaymentMethodId = 1,
                    Amount = 1m,
                    AmountBsS = 1m
                });
                await sales.SaveChangesAsync();
                break;

            case FinancialRow.CashDrawerSession:
                sales.CashDrawerSessions.Add(new CashDrawerSession());
                await sales.SaveChangesAsync();
                break;

            case FinancialRow.DailyClosure:
                sales.DailyClosures.Add(new DailyClosure());
                await sales.SaveChangesAsync();
                break;

            case FinancialRow.Receivable:
                sales.Receivables.Add(new Receivable { SaleId = 1, CustomerId = 1 });
                await sales.SaveChangesAsync();
                break;

            case FinancialRow.PettyCashExpense:
                sales.PettyCashExpenses.Add(new PettyCashExpense
                {
                    SessionId = 1,
                    VoucherNumber = 1,
                    CreatedByUserId = 1
                });
                await sales.SaveChangesAsync();
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(row), row, "Unknown financial row.");
        }
    }
}
