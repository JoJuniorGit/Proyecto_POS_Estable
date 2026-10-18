using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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
using SalesService = Sales.Module.Services.SalesService;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// F2a (Paso 11/12, S2/S6): cierre transaccional de venta a crédito (total y mixta) contra el
/// motor real de venta. La deuda se denomina en USD 4dp derivada del TotalBsS EFECTIVO
/// (financiadoBsS / tasa de emisión); el remanente financiado no genera caja ni SalePayment.
/// </summary>
public class CreditSaleCheckoutTests
{
    [Fact]
    public async Task CompleteCreditSale_FullCredit_CompletesSaleCreatesReceivableAndChargesNoCash()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        var sale = await SeedPendingSaleAsync(context, customer.Id, unitPriceUsd: 100m);
        var (service, inventory, _) = CreateHarness(context);

        var invoiceNumber = await service.CompleteCreditSaleAsync(
            sale.Id,
            new CreditSaleRequest { ExchangeRate = 50m, Payments = new List<SalePaymentDto>() });

        Assert.True(invoiceNumber > 0);

        var persisted = await context.Sales
            .AsNoTracking()
            .Include(candidate => candidate.Payments)
            .FirstAsync(candidate => candidate.Id == sale.Id);

        Assert.Equal(SaleStatus.Completed, persisted.Status);
        Assert.Equal(invoiceNumber, persisted.InvoiceNumber);
        Assert.Equal(100.00m, persisted.TotalUSD);
        Assert.Equal(5000.00m, persisted.TotalBsS);
        Assert.Equal(0m, persisted.FinalPaidAmountBsS);
        Assert.Empty(persisted.Payments);

        var receivable = await context.Receivables.AsNoTracking().SingleAsync(candidate => candidate.SaleId == sale.Id);
        Assert.Equal(customer.Id, receivable.CustomerId);
        Assert.Equal(100.0000m, receivable.OriginalUSD);
        Assert.Equal(100.0000m, receivable.BalanceUSD);
        Assert.Equal(5000.00m, receivable.OriginalBsS);
        Assert.Equal(50.0000m, receivable.HistoricalRate);
        Assert.Equal(ReceivableStatus.Vigente, receivable.Status);
        Assert.Equal(receivable.IssuedAtUtc.AddDays(30), receivable.DueDateUtc);
        Assert.True((DateTime.UtcNow - receivable.IssuedAtUtc).Duration() < TimeSpan.FromMinutes(1));

        // El monto financiado NO ingresa a la gaveta.
        Assert.Empty(await context.CashTransactions.AsNoTracking().Where(t => t.SaleId == sale.Id).ToListAsync());

        // El stock físico se descuenta en el instante de confirmar la venta.
        inventory.Verify(
            i => i.UpdateStockBatchAsync(
                It.IsAny<IEnumerable<StockDeductionRequest>>(),
                It.IsAny<string?>(),
                It.IsAny<bool>(),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task CompleteCreditSale_MixedPayment_ReceivableIsRemainderAndOnlyPaidAmountHitsDrawer()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        var sale = await SeedPendingSaleAsync(context, customer.Id, unitPriceUsd: 100m);
        await SeedPaymentMethodsAsync(context);
        var (service, _, _) = CreateHarness(context);

        var invoiceNumber = await service.CompleteCreditSaleAsync(
            sale.Id,
            new CreditSaleRequest
            {
                ExchangeRate = 50m,
                Payments = new List<SalePaymentDto>
                {
                    new() { PaymentMethodId = 1, Amount = 20m, AmountBsS = 1000m }
                }
            });

        Assert.True(invoiceNumber > 0);

        var persisted = await context.Sales
            .AsNoTracking()
            .Include(candidate => candidate.Payments)
            .FirstAsync(candidate => candidate.Id == sale.Id);

        Assert.Equal(SaleStatus.Completed, persisted.Status);
        var payment = Assert.Single(persisted.Payments);
        Assert.Equal(20m, payment.Amount);
        Assert.Equal(1000m, payment.AmountBsS);
        Assert.Equal(1000m, persisted.FinalPaidAmountBsS);

        var receivable = await context.Receivables.AsNoTracking().SingleAsync(candidate => candidate.SaleId == sale.Id);
        Assert.Equal(80.0000m, receivable.OriginalUSD);
        Assert.Equal(80.0000m, receivable.BalanceUSD);
        Assert.Equal(4000.00m, receivable.OriginalBsS);
        Assert.Equal(50.0000m, receivable.HistoricalRate);

        var cash = Assert.Single(await context.CashTransactions.AsNoTracking().Where(t => t.SaleId == sale.Id).ToListAsync());
        Assert.Equal(CashTransactionType.Income, cash.Type);
        Assert.Equal(1000m, cash.AmountLocal);
        Assert.True(cash.IsPhysicalCash);
    }

    [Fact]
    public async Task CompleteCreditSale_ExactMath_FinancedUsdUsesEffectiveBsSTotal()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        // 0.813 USD × 50 = 40.65 Bs.S (techo canónico por línea); el canónico USD redondea a 0.81.
        var sale = await SeedPendingSaleAsync(context, customer.Id, unitPriceUsd: 0.813m);
        var (service, _, _) = CreateHarness(context);

        await service.CompleteCreditSaleAsync(sale.Id, new CreditSaleRequest { ExchangeRate = 50m });

        var persisted = await context.Sales.AsNoTracking().FirstAsync(candidate => candidate.Id == sale.Id);
        Assert.Equal(0.81m, persisted.TotalUSD);
        Assert.Equal(40.65m, persisted.TotalBsS);

        var receivable = await context.Receivables.AsNoTracking().SingleAsync(candidate => candidate.SaleId == sale.Id);
        Assert.Equal(40.65m, receivable.OriginalBsS);
        Assert.Equal(0.8130m, receivable.OriginalUSD);
        Assert.Equal(50.0000m, receivable.HistoricalRate);
    }

    [Fact]
    public async Task CompleteCreditSale_CashStep5Rule_FinancesEffectiveBsSInsteadOfCanonicalUsd()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, creditLimitUsd: 10m);
        var sale = await SeedPendingSaleAsync(
            context,
            customer.Id,
            unitPriceUsd: 0.81m,
            roundingRule: RoundingRuleType.VesCashFriendlyStep5);
        var (service, _, _) = CreateHarness(context);

        await service.CompleteCreditSaleAsync(sale.Id, new CreditSaleRequest { ExchangeRate = 50m });

        var persisted = await context.Sales.AsNoTracking().FirstAsync(candidate => candidate.Id == sale.Id);
        Assert.Equal(0.81m, persisted.TotalUSD);
        // 0.81 × 50 = 40.50 → escalón ×5 inmediato superior = 45.00 Bs.S efectivos.
        Assert.Equal(45.00m, persisted.TotalBsS);

        var receivable = await context.Receivables.AsNoTracking().SingleAsync(candidate => candidate.SaleId == sale.Id);
        Assert.Equal(45.00m, receivable.OriginalBsS);
        // financiado USD = Bs.S efectivo / tasa de emisión (0.9000 ≠ canónico 0.81).
        Assert.Equal(0.9000m, receivable.OriginalUSD);
        Assert.Equal(0.9000m, receivable.BalanceUSD);
    }

    [Fact]
    public async Task CompleteCreditSale_DustRemainder_CompletesWithoutReceivable()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        var sale = await SeedPendingSaleAsync(context, customer.Id, unitPriceUsd: 100m);
        await SeedPaymentMethodsAsync(context);
        var (service, _, _) = CreateHarness(context);

        // 5000 − 4999.80 = 0.20 Bs.S → 0.0040 USD ≤ 0.005: polvo decimal, cierre regular.
        var invoiceNumber = await service.CompleteCreditSaleAsync(
            sale.Id,
            new CreditSaleRequest
            {
                ExchangeRate = 50m,
                Payments = new List<SalePaymentDto>
                {
                    new() { PaymentMethodId = 2, Amount = 0m, AmountBsS = 4999.80m }
                }
            });

        Assert.True(invoiceNumber > 0);

        var persisted = await context.Sales
            .AsNoTracking()
            .Include(candidate => candidate.Payments)
            .FirstAsync(candidate => candidate.Id == sale.Id);

        Assert.Equal(SaleStatus.Completed, persisted.Status);
        Assert.Single(persisted.Payments);
        Assert.Equal(0, await context.Receivables.AsNoTracking().CountAsync(candidate => candidate.SaleId == sale.Id));
    }

    [Fact]
    public async Task CompleteCreditSale_WithoutCustomer_IsRejected()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var sale = await SeedPendingSaleAsync(context, customerId: null, unitPriceUsd: 100m);
        var (service, _, _) = CreateHarness(context);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CompleteCreditSaleAsync(sale.Id, new CreditSaleRequest { ExchangeRate = 50m }));

        Assert.Contains("cliente registrado", ex.Message);
        Assert.Equal(0, await context.Receivables.AsNoTracking().CountAsync());
        Assert.Equal(SaleStatus.Pending, (await context.Sales.AsNoTracking().FirstAsync(candidate => candidate.Id == sale.Id)).Status);
    }

    [Fact]
    public async Task CompleteCreditSale_CreditDisabled_IsRejected()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, isCreditEnabled: false);
        var sale = await SeedPendingSaleAsync(context, customer.Id, unitPriceUsd: 100m);
        var (service, _, _) = CreateHarness(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteCreditSaleAsync(sale.Id, new CreditSaleRequest { ExchangeRate = 50m }));

        Assert.Contains("línea de crédito", ex.Message);
        Assert.Equal(0, await context.Receivables.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task CompleteCreditSale_ManuallyBlocked_IsRejected()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, creditStatus: CreditStatus.ManuallyBlocked);
        var sale = await SeedPendingSaleAsync(context, customer.Id, unitPriceUsd: 100m);
        var (service, _, _) = CreateHarness(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteCreditSaleAsync(sale.Id, new CreditSaleRequest { ExchangeRate = 50m }));

        Assert.Contains("bloqueado manualmente", ex.Message);
        Assert.Equal(0, await context.Receivables.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task CompleteCreditSale_SuspendedByMora_IsRejected()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, creditStatus: CreditStatus.SuspendedByMora);
        var sale = await SeedPendingSaleAsync(context, customer.Id, unitPriceUsd: 100m);
        var (service, _, _) = CreateHarness(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteCreditSaleAsync(sale.Id, new CreditSaleRequest { ExchangeRate = 50m }));

        Assert.Contains("morosidad", ex.Message);
        Assert.Equal(0, await context.Receivables.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task CompleteCreditSale_OverdueBeyondGrace_IsRejected()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, creditGraceDays: 3);
        await SeedOverdueReceivableAsync(context, customer.Id, balanceUsd: 10m, daysAgo: 10);
        var sale = await SeedPendingSaleAsync(context, customer.Id, unitPriceUsd: 100m);
        var (service, _, _) = CreateHarness(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteCreditSaleAsync(sale.Id, new CreditSaleRequest { ExchangeRate = 50m }));

        Assert.Contains("vencidas", ex.Message);
        Assert.Equal(0, await context.Receivables.AsNoTracking().CountAsync(candidate => candidate.SaleId == sale.Id));
        // La obligación vencida preexistente no se modifica.
        Assert.Equal(1, await context.Receivables.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task CompleteCreditSale_OverLimit_RequiresSupervisorAuthorization()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, creditLimitUsd: 50m);
        var sale = await SeedPendingSaleAsync(context, customer.Id, unitPriceUsd: 100m);
        var (service, _, _) = CreateHarness(context);

        // T3b1 (S4): el sobregiro de cupo ya no es un rechazo genérico; es la señal de dominio que
        // exige autorización de supervisor con el contexto exacto del cierre (T3b1 la consume).
        var ex = await Assert.ThrowsAsync<CreditOverLimitRequiresAuthorizationException>(() =>
            service.CompleteCreditSaleAsync(sale.Id, new CreditSaleRequest { ExchangeRate = 50m }));

        Assert.Contains("Crédito insuficiente", ex.Message);
        Assert.Equal(sale.Id, ex.SaleId);
        Assert.Equal(customer.Id, ex.CustomerId);
        Assert.Equal(100.0000m, ex.FinancedUSD);
        Assert.Equal(50.0000m, ex.AvailableCreditUSD);
        Assert.Equal(0, await context.Receivables.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task CompleteCreditSale_InvalidPaymentTerm_IsRejected()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context, paymentTermDays: 0);
        var sale = await SeedPendingSaleAsync(context, customer.Id, unitPriceUsd: 100m);
        var (service, _, _) = CreateHarness(context);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.CompleteCreditSaleAsync(sale.Id, new CreditSaleRequest { ExchangeRate = 50m }));

        Assert.Contains("plazo", ex.Message);
        Assert.Equal(0, await context.Receivables.AsNoTracking().CountAsync());
    }

    [Fact]
    public async Task CompleteCreditSale_Duplicate_ReturnsSameInvoiceAndDoesNotInsertSecondReceivable()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        var sale = await SeedPendingSaleAsync(context, customer.Id, unitPriceUsd: 100m);
        var (service, _, _) = CreateHarness(context);

        var request = new CreditSaleRequest { ExchangeRate = 50m };

        var firstInvoice = await service.CompleteCreditSaleAsync(sale.Id, request);
        var secondInvoice = await service.CompleteCreditSaleAsync(sale.Id, request);

        Assert.Equal(firstInvoice, secondInvoice);
        Assert.Equal(1, await context.Receivables.AsNoTracking().CountAsync(candidate => candidate.SaleId == sale.Id));
        Assert.Equal(SaleStatus.Completed, (await context.Sales.AsNoTracking().FirstAsync(candidate => candidate.Id == sale.Id)).Status);
    }

    [Fact]
    public async Task CompleteCreditSale_RegularCompleteStillRejectsUnderpaymentAndKeepsExistingBehavior()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerAsync(context);
        var sale = await SeedPendingSaleAsync(context, customer.Id, unitPriceUsd: 100m);
        await SeedPaymentMethodsAsync(context);
        var (service, _, _) = CreateHarness(context);

        // La ruta regular NO financia: un pago parcial sigue rechazado con el mensaje histórico.
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CompleteSaleAsync(sale.Id, 50m, new List<PaymentInfo>
            {
                new(1, 20m, 1000m, null)
            }));

        Assert.Contains("no cubre la totalidad de la venta", ex.Message);
        Assert.Equal(0, await context.Receivables.AsNoTracking().CountAsync());
    }

    private static (SalesService Service, Mock<IInventoryService> Inventory, Mock<ICashDrawerService> CashDrawer) CreateHarness(SalesDbContext context)
    {
        var inventory = new Mock<IInventoryService>();
        var mediator = new Mock<IMediator>();
        var cashDrawer = new Mock<ICashDrawerService>();
        var settings = new Mock<ISystemSettingsService>();

        cashDrawer
            .Setup(c => c.GetOrCreateActiveSessionAsync(It.IsAny<decimal>()))
            .ReturnsAsync(new CashDrawerSessionResponseDto { Id = 1, Status = CashDrawerStatus.Open });

        var service = new SalesService(
            context,
            inventory.Object,
            mediator.Object,
            cashDrawer.Object,
            settings.Object,
            creditService: new CreditService(context));

        return (service, inventory, cashDrawer);
    }

    private static async Task<Customer> SeedCustomerAsync(
        SalesDbContext context,
        decimal creditLimitUsd = 1000m,
        int paymentTermDays = 30,
        int creditGraceDays = 3,
        bool isCreditEnabled = true,
        CreditStatus creditStatus = CreditStatus.Active)
    {
        var customer = new Customer
        {
            CedulaOrRif = "V-" + Guid.NewGuid().ToString("N").Substring(0, 8),
            Name = "Cliente Credito POS",
            IsCreditEnabled = isCreditEnabled,
            CreditLimitUSD = creditLimitUsd,
            PaymentTermDays = paymentTermDays,
            CreditGraceDays = creditGraceDays,
            CreditStatus = creditStatus,
            IsActive = true
        };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
        return customer;
    }

    private static async Task<Sale> SeedPendingSaleAsync(
        SalesDbContext context,
        int? customerId,
        decimal unitPriceUsd,
        RoundingRuleType? roundingRule = null)
    {
        var sale = new Sale
        {
            CustomerId = customerId,
            Status = SaleStatus.Pending,
            DeliveryStatus = SaleDeliveryStatus.PendingPickup,
            PriceListType = "Retail"
        };
        sale.Items.Add(new SaleItem
        {
            ProductId = 101,
            ProductName = "Harina Pan",
            Quantity = 1m,
            UnitPrice = unitPriceUsd,
            IsCustomPrice = false,
            RoundingRuleCashBs = roundingRule
        });
        context.Sales.Add(sale);
        await context.SaveChangesAsync();
        return sale;
    }

    private static async Task SeedPaymentMethodsAsync(SalesDbContext context)
    {
        context.PaymentMethods.AddRange(
            new PaymentMethod { Id = 1, Name = "Efectivo USD", IsCash = true, IsActive = true, DisplayOrder = 1 },
            new PaymentMethod { Id = 2, Name = "Punto de Venta", IsCash = false, IsActive = true, DisplayOrder = 2 });
        await context.SaveChangesAsync();
    }

    private static async Task SeedOverdueReceivableAsync(SalesDbContext context, int customerId, decimal balanceUsd, int daysAgo)
    {
        var previousSale = new Sale
        {
            CustomerId = customerId,
            Status = SaleStatus.Completed,
            InvoiceNumber = 9001,
            Date = DateTime.UtcNow.AddDays(-daysAgo),
            AppliedRate = 50m,
            TotalUSD = balanceUsd,
            TotalBsS = Math.Round(balanceUsd * 50m, 2, MidpointRounding.AwayFromZero)
        };
        context.Sales.Add(previousSale);
        await context.SaveChangesAsync();

        context.Receivables.Add(new Receivable
        {
            SaleId = previousSale.Id,
            CustomerId = customerId,
            OriginalUSD = balanceUsd,
            BalanceUSD = balanceUsd,
            OriginalBsS = Math.Round(balanceUsd * 50m, 2, MidpointRounding.AwayFromZero),
            HistoricalRate = 50m,
            IssuedAtUtc = DateTime.UtcNow.AddDays(-daysAgo),
            DueDateUtc = DateTime.UtcNow.AddDays(-daysAgo),
            Status = ReceivableStatus.Vigente
        });
        await context.SaveChangesAsync();
    }
}
