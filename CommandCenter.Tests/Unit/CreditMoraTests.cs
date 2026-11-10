using System;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.Entities;
using Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// H1 (T6, S3/S4): barrido diario de cartera — materialización del estado Vencida, suspensión
/// automática por mora, reactivación al regularizar e idempotencia. La mora usa la regla
/// compartida (vencimiento + gracia vs. día Venezuela) con bordes exactos y polvo decimal.
/// </summary>
public class CreditMoraTests
{
    private static readonly DateTime FixedUtc = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task EvaluateOverdueAndMoraAsync_MaterializesOpenOverdueRespectingGraceAndExactBoundary()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();

        // Sin gracia: vencida ayer (materializa) y vence hoy (borde exacto: NO materializa).
        var noGrace = BuildCustomer(graceDays: 0, creditStatus: CreditStatus.ManuallyBlocked);
        // Con gracia 3: vence hace 3 (borde exacto: NO materializa) y hace 4 (materializa).
        var withGrace = BuildCustomer(graceDays: 3);
        context.Customers.AddRange(noGrace, withGrace);
        await context.SaveChangesAsync();

        var overdueYesterday = await AddReceivableAsync(context, noGrace.Id, 100m, ReceivableStatus.Vigente, VenezuelaToday(-1));
        var dueToday = await AddReceivableAsync(context, noGrace.Id, 50m, ReceivableStatus.Parcial, VenezuelaToday(0));
        var exactBoundary = await AddReceivableAsync(context, withGrace.Id, 40m, ReceivableStatus.Vigente, VenezuelaToday(-3));
        var beyondGrace = await AddReceivableAsync(context, withGrace.Id, 60m, ReceivableStatus.Parcial, VenezuelaToday(-4));

        var result = await new CreditService(context).EvaluateOverdueAndMoraAsync();

        Assert.Equal(2, result.OverdueMaterializedCount);
        Assert.Equal(2, result.EvaluatedCustomersCount);
        // withGrace pasa a mora; noGrace es ManuallyBlocked y nunca se toca.
        Assert.Equal(1, result.SuspendedCount);
        Assert.Equal(0, result.ReactivatedCount);

        Assert.Equal(ReceivableStatus.Vencida, await GetReceivableStatusAsync(context, overdueYesterday.Id));
        Assert.Equal(ReceivableStatus.Vencida, await GetReceivableStatusAsync(context, beyondGrace.Id));
        Assert.Equal(ReceivableStatus.Parcial, await GetReceivableStatusAsync(context, dueToday.Id));
        Assert.Equal(ReceivableStatus.Vigente, await GetReceivableStatusAsync(context, exactBoundary.Id));

        Assert.Equal(CreditStatus.SuspendedByMora, await GetCustomerStatusAsync(context, withGrace.Id));
        Assert.Equal(CreditStatus.ManuallyBlocked, await GetCustomerStatusAsync(context, noGrace.Id));
    }

    [Fact]
    public async Task EvaluateOverdueAndMoraAsync_LeavesClosedObligationsAndDustUntouched()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer(graceDays: 0);
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        var paid = await AddReceivableAsync(context, customer.Id, 100m, ReceivableStatus.Pagada, VenezuelaToday(-30));
        var annulled = await AddReceivableAsync(context, customer.Id, 80m, ReceivableStatus.Anulada, VenezuelaToday(-30));
        // Polvo decimal (≤ 0.005 USD): no computa deuda ni mora, y no se materializa.
        var dust = await AddReceivableAsync(context, customer.Id, 0.0040m, ReceivableStatus.Vigente, VenezuelaToday(-30));

        var result = await new CreditService(context).EvaluateOverdueAndMoraAsync();

        Assert.Equal(0, result.OverdueMaterializedCount);
        Assert.Equal(0, result.SuspendedCount);
        Assert.Equal(0, result.ReactivatedCount);
        Assert.Equal(1, result.EvaluatedCustomersCount);

        Assert.Equal(ReceivableStatus.Pagada, await GetReceivableStatusAsync(context, paid.Id));
        Assert.Equal(ReceivableStatus.Anulada, await GetReceivableStatusAsync(context, annulled.Id));
        Assert.Equal(ReceivableStatus.Vigente, await GetReceivableStatusAsync(context, dust.Id));
        Assert.Equal(CreditStatus.Active, await GetCustomerStatusAsync(context, customer.Id));
    }

    [Fact]
    public async Task EvaluateOverdueAndMoraAsync_SuspendsActiveAndKeepsManuallyBlockedAndDisabled()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var active = BuildCustomer(graceDays: 0);
        var blocked = BuildCustomer(graceDays: 0, creditStatus: CreditStatus.ManuallyBlocked);
        var disabled = BuildCustomer(graceDays: 0, isCreditEnabled: false);
        context.Customers.AddRange(active, blocked, disabled);
        await context.SaveChangesAsync();

        // La materialización es un hecho de cartera: alcanza también a clientes bloqueados o sin
        // línea habilitada; la suspensión solo aplica a los habilitados y activos.
        await AddReceivableAsync(context, active.Id, 100m, ReceivableStatus.Vigente, VenezuelaToday(-1));
        await AddReceivableAsync(context, blocked.Id, 100m, ReceivableStatus.Vigente, VenezuelaToday(-1));
        await AddReceivableAsync(context, disabled.Id, 100m, ReceivableStatus.Vigente, VenezuelaToday(-1));

        var result = await new CreditService(context).EvaluateOverdueAndMoraAsync();

        Assert.Equal(3, result.OverdueMaterializedCount);
        // Evaluados: active + blocked (ambos con línea habilitada); disabled queda fuera.
        Assert.Equal(2, result.EvaluatedCustomersCount);
        Assert.Equal(1, result.SuspendedCount);
        Assert.Equal(0, result.ReactivatedCount);

        Assert.Equal(CreditStatus.SuspendedByMora, await GetCustomerStatusAsync(context, active.Id));
        Assert.Equal(CreditStatus.ManuallyBlocked, await GetCustomerStatusAsync(context, blocked.Id));
        Assert.Equal(CreditStatus.Active, await GetCustomerStatusAsync(context, disabled.Id));
    }

    [Fact]
    public async Task EvaluateOverdueAndMoraAsync_ReactivatesSuspendedCustomerWithoutOpenOverdue()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer(graceDays: 3, creditStatus: CreditStatus.SuspendedByMora);
        context.Customers.Add(customer);
        await context.SaveChangesAsync();

        // Sin vencidas abiertas: una pagada y una vigente aún dentro del período de gracia.
        await AddReceivableAsync(context, customer.Id, 100m, ReceivableStatus.Pagada, VenezuelaToday(-30));
        await AddReceivableAsync(context, customer.Id, 50m, ReceivableStatus.Vigente, VenezuelaToday(-2));

        var result = await new CreditService(context).EvaluateOverdueAndMoraAsync();

        Assert.Equal(0, result.OverdueMaterializedCount);
        Assert.Equal(0, result.SuspendedCount);
        Assert.Equal(1, result.ReactivatedCount);
        Assert.Equal(1, result.EvaluatedCustomersCount);
        Assert.Equal(CreditStatus.Active, await GetCustomerStatusAsync(context, customer.Id));
    }

    [Fact]
    public async Task EvaluateOverdueAndMoraAsync_KeepsSuspendedCustomerWhileStillOverdue()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer(graceDays: 0, creditStatus: CreditStatus.SuspendedByMora);
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
        await AddReceivableAsync(context, customer.Id, 100m, ReceivableStatus.Vigente, VenezuelaToday(-1));

        var result = await new CreditService(context).EvaluateOverdueAndMoraAsync();

        Assert.Equal(1, result.OverdueMaterializedCount);
        Assert.Equal(0, result.SuspendedCount);
        Assert.Equal(0, result.ReactivatedCount);
        Assert.Equal(CreditStatus.SuspendedByMora, await GetCustomerStatusAsync(context, customer.Id));
    }

    [Fact]
    public async Task EvaluateOverdueAndMoraAsync_SecondRunIsIdempotent()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer(graceDays: 0);
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
        var receivable = await AddReceivableAsync(context, customer.Id, 100m, ReceivableStatus.Vigente, VenezuelaToday(-1));

        var service = new CreditService(context);
        var first = await service.EvaluateOverdueAndMoraAsync();
        var second = await service.EvaluateOverdueAndMoraAsync();

        Assert.Equal(1, first.OverdueMaterializedCount);
        Assert.Equal(1, first.SuspendedCount);
        Assert.Equal(0, first.ReactivatedCount);

        Assert.Equal(0, second.OverdueMaterializedCount);
        Assert.Equal(0, second.SuspendedCount);
        Assert.Equal(0, second.ReactivatedCount);
        Assert.Equal(1, second.EvaluatedCustomersCount);

        Assert.Equal(ReceivableStatus.Vencida, await GetReceivableStatusAsync(context, receivable.Id));
        Assert.Equal(CreditStatus.SuspendedByMora, await GetCustomerStatusAsync(context, customer.Id));
    }

    [Fact]
    public async Task EvaluateOverdueAndMoraAsync_DoesNotTouchBalancesSnapshotsOrPayments()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var customer = BuildCustomer(graceDays: 0);
        context.Customers.Add(customer);
        await context.SaveChangesAsync();
        var receivable = await AddReceivableAsync(context, customer.Id, 100m, ReceivableStatus.Vigente, VenezuelaToday(-3));

        var result = await new CreditService(context).EvaluateOverdueAndMoraAsync();

        Assert.Equal(1, result.OverdueMaterializedCount);

        var reloaded = await context.Receivables.AsNoTracking().FirstAsync(candidate => candidate.Id == receivable.Id);
        Assert.Equal(100.0000m, reloaded.OriginalUSD);
        Assert.Equal(100.0000m, reloaded.BalanceUSD);
        Assert.Equal(5000.00m, reloaded.OriginalBsS);
        Assert.Equal(50m, reloaded.HistoricalRate);
        Assert.Equal(receivable.IssuedAtUtc, reloaded.IssuedAtUtc);
        Assert.Equal(receivable.DueDateUtc, reloaded.DueDateUtc);
        Assert.Equal(0, await context.ReceivablePayments.CountAsync());
        Assert.Equal(0, await context.CreditPaymentAllocations.CountAsync());
    }

    private static Customer BuildCustomer(
        int graceDays = 0,
        bool isCreditEnabled = true,
        CreditStatus creditStatus = CreditStatus.Active)
    {
        return new Customer
        {
            CedulaOrRif = "V-" + Guid.NewGuid().ToString("N").Substring(0, 8),
            Name = "Cliente Mora Test",
            IsCreditEnabled = isCreditEnabled,
            CreditLimitUSD = 1000m,
            PaymentTermDays = 30,
            CreditGraceDays = graceDays,
            CreditStatus = creditStatus,
            IsActive = true
        };
    }

    private static DateTime VenezuelaToday(int offsetDays)
    {
        var today = TimeZoneHelper.GetVenezuelaDate();
        return today.AddDays(offsetDays).ToDateTime(TimeOnly.MinValue);
    }

    private static async Task<Receivable> AddReceivableAsync(
        SalesDbContext context,
        int customerId,
        decimal balanceUsd,
        ReceivableStatus status,
        DateTime dueDateUtc)
    {
        var sale = new Sale
        {
            Status = SaleStatus.Completed,
            Date = FixedUtc,
            AppliedRate = 50m,
            TotalUSD = balanceUsd,
            TotalBsS = Math.Round(balanceUsd * 50m, 2, MidpointRounding.AwayFromZero)
        };
        context.Sales.Add(sale);
        await context.SaveChangesAsync();

        var receivable = new Receivable
        {
            SaleId = sale.Id,
            CustomerId = customerId,
            OriginalUSD = balanceUsd,
            BalanceUSD = balanceUsd,
            OriginalBsS = Math.Round(balanceUsd * 50m, 2, MidpointRounding.AwayFromZero),
            HistoricalRate = 50m,
            IssuedAtUtc = FixedUtc,
            DueDateUtc = dueDateUtc,
            Status = status
        };
        context.Receivables.Add(receivable);
        await context.SaveChangesAsync();
        return receivable;
    }

    private static async Task<ReceivableStatus> GetReceivableStatusAsync(SalesDbContext context, int receivableId)
    {
        return await context.Receivables
            .AsNoTracking()
            .Where(candidate => candidate.Id == receivableId)
            .Select(candidate => candidate.Status)
            .FirstAsync();
    }

    private static async Task<CreditStatus> GetCustomerStatusAsync(SalesDbContext context, int customerId)
    {
        return await context.Customers
            .AsNoTracking()
            .Where(candidate => candidate.Id == customerId)
            .Select(candidate => candidate.CreditStatus)
            .FirstAsync();
    }
}
