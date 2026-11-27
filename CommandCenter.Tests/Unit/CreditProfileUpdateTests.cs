using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Entities;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// T4-prep (Paso 11, S1/S4): contrato de escritura del perfil crediticio del cliente para la UI
/// administrativa. El estado Suspendido por Mora es system-managed y jamás se acepta desde la
/// edición manual; el resto del perfil (habilitación, cupo, plazo, gracia y notas) se valida y
/// persiste sobre la entidad rastreada para que el token xmin de Customer siga vigente.
/// </summary>
public class CreditProfileUpdateTests
{
    [Fact]
    public async Task UpdateCustomerCreditProfileAsync_PersistsAllFieldsAndReturnsRecomputedProfile()
    {
        var databaseName = Guid.NewGuid().ToString();
        using var context = TestDatabaseFactory.CreateSalesDbContext(databaseName);
        var customer = BuildCustomer();
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var service = new CreditService(context);
        var profile = await service.UpdateCustomerCreditProfileAsync(customer.Id, new CustomerCreditProfileUpdateDto
        {
            IsCreditEnabled = true,
            CreditLimitUSD = 500m,
            PaymentTermDays = 45,
            CreditGraceDays = 7,
            CreditNotes = "  Garantía personal  ",
            CreditStatus = CreditStatus.Active
        });

        Assert.Equal(customer.Id, profile.CustomerId);
        Assert.True(profile.IsCreditEnabled);
        Assert.Equal(500m, profile.CreditLimitUSD);
        Assert.Equal(45, profile.PaymentTermDays);
        Assert.Equal(7, profile.CreditGraceDays);
        Assert.Equal("Garantía personal", profile.CreditNotes);
        Assert.Equal(CreditStatus.Active, profile.CreditStatus);

        using var verification = TestDatabaseFactory.CreateSalesDbContext(databaseName);
        var persisted = await verification.Customers.AsNoTracking().SingleAsync(candidate => candidate.Id == customer.Id);
        Assert.True(persisted.IsCreditEnabled);
        Assert.Equal(500m, persisted.CreditLimitUSD);
        Assert.Equal(45, persisted.PaymentTermDays);
        Assert.Equal(7, persisted.CreditGraceDays);
        Assert.Equal("Garantía personal", persisted.CreditNotes);
        Assert.Equal(CreditStatus.Active, persisted.CreditStatus);
    }

    [Fact]
    public async Task UpdateCustomerCreditProfileAsync_ManualBlockAndUnblock_RoundTripsStatus()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer();
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
        var service = new CreditService(context);

        var blocked = await service.UpdateCustomerCreditProfileAsync(customer.Id, BuildUpdate(status: CreditStatus.ManuallyBlocked));
        Assert.Equal(CreditStatus.ManuallyBlocked, blocked.CreditStatus);

        var unblocked = await service.UpdateCustomerCreditProfileAsync(customer.Id, BuildUpdate(status: CreditStatus.Active));
        Assert.Equal(CreditStatus.Active, unblocked.CreditStatus);
    }

    [Fact]
    public async Task UpdateCustomerCreditProfileAsync_RejectsSuspendedByMoraManualAssignment()
    {
        var databaseName = Guid.NewGuid().ToString();
        using var context = TestDatabaseFactory.CreateSalesDbContext(databaseName);
        var customer = BuildCustomer();
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
        var service = new CreditService(context);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.UpdateCustomerCreditProfileAsync(customer.Id, BuildUpdate(status: CreditStatus.SuspendedByMora)));

        Assert.Contains("mora", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("sistema", ex.Message, StringComparison.OrdinalIgnoreCase);
        using var verification = TestDatabaseFactory.CreateSalesDbContext(databaseName);
        Assert.Equal(CreditStatus.Active, (await verification.Customers.AsNoTracking().SingleAsync()).CreditStatus);
    }

    [Fact]
    public async Task UpdateCustomerCreditProfileAsync_RejectsNegativeCreditLimit()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer();
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            new CreditService(context).UpdateCustomerCreditProfileAsync(customer.Id, BuildUpdate(limit: -0.01m)));

        Assert.Contains("límite", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(366)]
    public async Task UpdateCustomerCreditProfileAsync_RejectsPaymentTermOutsideRange(int termDays)
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer();
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            new CreditService(context).UpdateCustomerCreditProfileAsync(customer.Id, BuildUpdate(termDays: termDays)));

        Assert.Contains("plazo", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("365", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(60)]
    [InlineData(365)]
    public async Task UpdateCustomerCreditProfileAsync_AcceptsRecommendedAndBoundaryTerms(int termDays)
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer();
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var profile = await new CreditService(context).UpdateCustomerCreditProfileAsync(
            customer.Id,
            BuildUpdate(termDays: termDays));

        Assert.Equal(termDays, profile.PaymentTermDays);
    }

    [Fact]
    public async Task UpdateCustomerCreditProfileAsync_RejectsNegativeGraceDays()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer();
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            new CreditService(context).UpdateCustomerCreditProfileAsync(customer.Id, BuildUpdate(graceDays: -1)));

        Assert.Contains("gracia", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UpdateCustomerCreditProfileAsync_RejectsNotesLongerThan1000()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer();
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            new CreditService(context).UpdateCustomerCreditProfileAsync(
                customer.Id,
                BuildUpdate(notes: new string('x', 1001))));

        Assert.Contains("1000", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpdateCustomerCreditProfileAsync_AllowsEmptyNotesAndZeroLimit()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer();
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var profile = await new CreditService(context).UpdateCustomerCreditProfileAsync(
            customer.Id,
            BuildUpdate(limit: 0m, notes: "   ", status: CreditStatus.ManuallyBlocked));

        Assert.Equal(0m, profile.CreditLimitUSD);
        Assert.Null(profile.CreditNotes);
        Assert.Equal(CreditStatus.ManuallyBlocked, profile.CreditStatus);
    }

    [Fact]
    public async Task UpdateCustomerCreditProfileAsync_MissingCustomer_ThrowsKeyNotFound()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        await Assert.ThrowsAsync<System.Collections.Generic.KeyNotFoundException>(() =>
            new CreditService(context).UpdateCustomerCreditProfileAsync(9999, BuildUpdate()));
    }

    [Fact]
    public async Task UpdateCustomerCreditProfileAsync_TracksCustomerBoundToXminConcurrencyToken()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var context = new TrackingSalesDbContext(options);
        var customer = BuildCustomer();
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var service = new CreditService(context);
        await service.UpdateCustomerCreditProfileAsync(customer.Id, BuildUpdate(limit: 250m));

        Assert.True(context.CustomerModifiedOnSave);
        var xmin = context.Model.FindEntityType(typeof(Customer))!.FindProperty("xmin");
        Assert.NotNull(xmin);
        Assert.True(xmin!.IsConcurrencyToken);
    }

    private static CustomerCreditProfileUpdateDto BuildUpdate(
        decimal limit = 100m,
        int termDays = 30,
        int graceDays = 0,
        string? notes = null,
        CreditStatus status = CreditStatus.Active) => new()
    {
        IsCreditEnabled = true,
        CreditLimitUSD = limit,
        PaymentTermDays = termDays,
        CreditGraceDays = graceDays,
        CreditNotes = notes,
        CreditStatus = status
    };

    private static Customer BuildCustomer() => new()
    {
        CedulaOrRif = "V-" + Guid.NewGuid().ToString("N")[..8],
        Name = "Cliente Perfil Test",
        IsCreditEnabled = false,
        CreditLimitUSD = 0m,
        PaymentTermDays = 30,
        CreditGraceDays = 0,
        CreditStatus = CreditStatus.Active,
        IsActive = true
    };

    private sealed class TrackingSalesDbContext : SalesDbContext
    {
        public TrackingSalesDbContext(DbContextOptions<SalesDbContext> options)
            : base(options)
        {
        }

        public bool CustomerModifiedOnSave { get; private set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            CustomerModifiedOnSave |= ChangeTracker.Entries<Customer>()
                .Any(entry => entry.State == EntityState.Modified);
            return base.SaveChangesAsync(cancellationToken);
        }
    }
}
