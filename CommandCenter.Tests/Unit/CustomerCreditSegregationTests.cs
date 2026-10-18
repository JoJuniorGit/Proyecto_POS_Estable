using System;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.187/8.188 (Pasos 11-13): segregación de funciones entre contacto y crédito. El alta y la
/// edición de contacto descartan server-side los campos crediticios para TODOS los roles (el
/// crédito solo se escribe por PUT credit-profile, Admin/Manager) y la eliminación física queda
/// bloqueada por cualquier rastro de cartera (saldo o historial) o abonos del cliente.
/// </summary>
public class CustomerCreditSegregationTests
{
    private static SalesService CreateSalesService(SalesDbContext context)
    {
        var mockInventory = new Mock<IInventoryService>();
        var mockMediator = new Mock<IMediator>();
        var mockCashDrawer = new Mock<ICashDrawerService>();
        var mockSettings = new Mock<ISystemSettingsService>();
        return new SalesService(context, mockInventory.Object, mockMediator.Object, mockCashDrawer.Object, mockSettings.Object);
    }

    private static async Task<Customer> SeedCreditCustomerAsync(SalesDbContext context)
    {
        var customer = new Customer
        {
            CedulaOrRif = "V-19000111",
            Name = "Cliente Crédito",
            Phone = "0412-1234567",
            CreditLimitUSD = 500m,
            IsCreditEnabled = true,
            PaymentTermDays = 45,
            CreditGraceDays = 5,
            CreditStatus = CreditStatus.ManuallyBlocked,
            CreditNotes = "Acuerdo original",
            IsActive = true
        };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
        return customer;
    }

    private static async Task<Customer> SeedCustomerWithPortfolioAsync(
        SalesDbContext context,
        ReceivableStatus status,
        decimal balanceUsd)
    {
        var customer = new Customer
        {
            CedulaOrRif = "V-19000999",
            Name = "Cliente Con Cartera",
            IsActive = true
        };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var sale = new Sale
        {
            CustomerId = customer.Id,
            Status = SaleStatus.Completed,
            Date = DateTime.UtcNow,
            AppliedRate = 50m,
            TotalUSD = balanceUsd,
            TotalBsS = Math.Round(balanceUsd * 50m, 2, MidpointRounding.AwayFromZero)
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        context.Receivables.Add(new Receivable
        {
            SaleId = sale.Id,
            CustomerId = customer.Id,
            OriginalUSD = balanceUsd,
            BalanceUSD = balanceUsd,
            OriginalBsS = Math.Round(balanceUsd * 50m, 2, MidpointRounding.AwayFromZero),
            HistoricalRate = 50m,
            IssuedAtUtc = DateTime.UtcNow,
            DueDateUtc = DateTime.UtcNow.AddDays(30),
            Status = status
        });
        await context.SaveChangesAsync();
        return customer;
    }

    [Fact]
    public async Task UpdateCustomer_IgnoresCreditFieldsOfTheContactPayloadForEveryRole()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCreditCustomerAsync(context);

        var updated = await CreateSalesService(context).UpdateCustomerAsync(customer.Id, new UpdateCustomerDto
        {
            CedulaOrRif = "V-19000111",
            Name = "Cliente Contacto Editado",
            Phone = "0414-7654321",
            CreditLimitUSD = 9999m, // intento de subir el cupo por la vía de contacto
            IsActive = true
        });

        var persisted = await context.Customers.AsNoTracking().SingleAsync(candidate => candidate.Id == customer.Id);
        Assert.Equal("Cliente Contacto Editado", persisted.Name);
        Assert.Equal("0414-7654321", persisted.Phone);

        // Los parámetros crediticios sobreviven intactos al update de contacto.
        Assert.Equal(500m, persisted.CreditLimitUSD);
        Assert.True(persisted.IsCreditEnabled);
        Assert.Equal(45, persisted.PaymentTermDays);
        Assert.Equal(5, persisted.CreditGraceDays);
        Assert.Equal(CreditStatus.ManuallyBlocked, persisted.CreditStatus);
        Assert.Equal("Acuerdo original", persisted.CreditNotes);
        Assert.Equal(500m, updated.CreditLimitUSD);
    }

    [Fact]
    public async Task UpdateCustomer_CanLowerContactFieldsButNeverTheCreditLimit()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCreditCustomerAsync(context);

        await CreateSalesService(context).UpdateCustomerAsync(customer.Id, new UpdateCustomerDto
        {
            CedulaOrRif = "V-19000111",
            Name = "Cliente Crédito",
            Phone = "0412-1234567",
            CreditLimitUSD = 1m, // intento de bajar el cupo por debajo de la deuda
            IsActive = true
        });

        var persisted = await context.Customers.AsNoTracking().SingleAsync(candidate => candidate.Id == customer.Id);
        Assert.Equal(500m, persisted.CreditLimitUSD);
    }

    [Fact]
    public async Task CreateCustomer_IgnoresCreditLimitOfTheContactPayload()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        var created = await CreateSalesService(context).CreateCustomerAsync(new CreateCustomerDto
        {
            CedulaOrRif = "V-19000222",
            Name = "Cliente Nuevo",
            Phone = "0412-1112233",
            CreditLimitUSD = 750m // la línea no se abre por la vía de contacto
        });

        var persisted = await context.Customers.AsNoTracking().SingleAsync(candidate => candidate.Id == created.Id);
        Assert.Equal(0m, persisted.CreditLimitUSD);
        Assert.False(persisted.IsCreditEnabled);
        Assert.Equal(0m, created.CreditLimitUSD);
    }

    [Fact]
    public async Task DeleteCustomer_WithOpenReceivableBalance_IsBlockedWithPortfolioMessage()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerWithPortfolioAsync(context, ReceivableStatus.Vigente, balanceUsd: 100m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSalesService(context).DeleteCustomerAsync(customer.Id));

        Assert.Contains("cuentas por cobrar", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(await context.Customers.AsNoTracking().AnyAsync(candidate => candidate.Id == customer.Id));
    }

    [Fact]
    public async Task DeleteCustomer_WithPaidReceivableHistory_IsBlockedEvenWithoutBalance()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = await SeedCustomerWithPortfolioAsync(context, ReceivableStatus.Pagada, balanceUsd: 0m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSalesService(context).DeleteCustomerAsync(customer.Id));

        Assert.Contains("cuentas por cobrar", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(await context.Customers.AsNoTracking().AnyAsync(candidate => candidate.Id == customer.Id));
    }

    [Fact]
    public async Task DeleteCustomer_WithPortfolioPayments_IsBlockedWithPaymentHistoryMessage()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = new Customer { CedulaOrRif = "V-19000333", Name = "Cliente Con Abonos", IsActive = true };
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        context.ReceivablePayments.Add(new ReceivablePayment
        {
            CustomerId = customer.Id,
            AmountUSD = 100m,
            AmountBsS = 5000m,
            PaymentRate = 50m,
            FxDifferenceBsS = 0m,
            ReceivedAtUtc = DateTime.UtcNow
        });
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => CreateSalesService(context).DeleteCustomerAsync(customer.Id));

        Assert.Contains("abonos de cartera", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(await context.Customers.AsNoTracking().AnyAsync(candidate => candidate.Id == customer.Id));
    }

    [Fact]
    public async Task DeleteCustomer_WithoutPortfolioOrSales_StillDeletes()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        context.Customers.Add(new Customer { Id = 42, CedulaOrRif = "V-19000444", Name = "Cliente Libre", IsActive = true });
        await context.SaveChangesAsync();

        await CreateSalesService(context).DeleteCustomerAsync(42);

        Assert.False(await context.Customers.AsNoTracking().AnyAsync(candidate => candidate.Id == 42));
    }
}
