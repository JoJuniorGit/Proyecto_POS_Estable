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
using Npgsql;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using Xunit;
using SalesService = Sales.Module.Services.SalesService;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// Carril PostgreSQL real (Paso 11-13 / 8.187-8.188): comportamientos Postgres-only ejercitados
/// contra Npgsql real en bases AISLADAS por test (ver <see cref="IsolatedPostgresDatabase"/>).
/// Cubre: advisory lock clase 3 (abonos CxC), clase 4 (barrido de mora), conflicto xmin con
/// resolución esperada, nextval del correlativo de caja chica y precisión 4dp del bucket USD.
/// Sin conexión utilizable el carril falla EXPLÍCITO (nunca silent-pass) desde el helper de lane.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(PostgresRealCollection.Name)]
public class PostgresRealLaneTests
{
    private const decimal TestBcvRate = 60m;

    // ---------------------------------------------------------------- advisory lock clase 3 (CxC)

    [Fact]
    public async Task RegisterCreditPaymentAsync_Class3Lock_BlocksSameCustomerUntilReleasedThenSettles()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(seedStandardSalesData: true);

        (int customerId, int receivableId, int paymentMethodId) = await SeedCreditScenarioAsync(database, balanceUsd: 100m, dueInDays: 10);

        // Candado externo (otra sesión) sobre la clave (3, cliente): el abono debe esperar.
        await using var gate = new NpgsqlConnection(database.ConnectionString);
        await gate.OpenAsync();
        await using var gateTransaction = await gate.BeginTransactionAsync();
        await using (var gateCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(3, @key)", gate, gateTransaction))
        {
            gateCommand.Parameters.AddWithValue("key", customerId);
            await gateCommand.ExecuteScalarAsync();
        }

        var payment = Task.Run(async () =>
        {
            await using var context = database.CreateSalesDbContext();
            return await CreateSalesService(context, TestBcvRate).RegisterCreditPaymentAsync(new CreditPaymentRequest
            {
                CustomerId = customerId,
                AmountUSD = 60m,
                PaymentMethodId = paymentMethodId
            });
        });

        bool blockedOnClass3 = await WaitForUngrantedAdvisoryLockAsync(database.ConnectionString, lockClass: 3, lockKey: customerId, TimeSpan.FromSeconds(20));
        Assert.True(blockedOnClass3, "El abono CxC debe quedar esperando el advisory lock por cliente (clase 3) mientras otra sesión lo retiene.");

        await gateTransaction.RollbackAsync();

        var result = await payment.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(60m, result.AmountUSD);

        await using var verifyContext = database.CreateSalesDbContext();
        var persisted = await verifyContext.Receivables.AsNoTracking().SingleAsync(receivable => receivable.Id == receivableId);
        Assert.Equal(40m, persisted.BalanceUSD);
        Assert.Equal(ReceivableStatus.Parcial, persisted.Status);
    }

    [Fact]
    public async Task ConcurrentAbonos_SameCustomer_SerializeAndRejectOverpayWithoutOverdrawing()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(seedStandardSalesData: true);

        (int customerId, int receivableId, int paymentMethodId) = await SeedCreditScenarioAsync(database, balanceUsd: 100m, dueInDays: 10);

        var request = new CreditPaymentRequest
        {
            CustomerId = customerId,
            AmountUSD = 60m,
            PaymentMethodId = paymentMethodId
        };

        var failures = await Task.WhenAll(
            Task.Run(() => TryRegisterPaymentAsync(database, request)),
            Task.Run(() => TryRegisterPaymentAsync(database, request)));

        Assert.Equal(1, failures.Count(failure => failure is null));
        var rejection = Assert.IsType<ArgumentException>(Assert.Single(failures, failure => failure is not null));
        Assert.Contains("excede el saldo pendiente", rejection.Message);

        await using var verifyContext = database.CreateSalesDbContext();
        var persisted = await verifyContext.Receivables.AsNoTracking().SingleAsync(receivable => receivable.Id == receivableId);
        Assert.Equal(40m, persisted.BalanceUSD);
        Assert.Equal(ReceivableStatus.Parcial, persisted.Status);
        Assert.Equal(1, await verifyContext.ReceivablePayments.CountAsync(payment => payment.CustomerId == customerId));
    }

    // ---------------------------------------------------------------- advisory lock clase 4 (mora)

    [Fact]
    public async Task EvaluateOverdueAndMoraAsync_Class4Lock_BlocksGlobalSweepUntilReleased()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(seedStandardSalesData: true);
        (int customerId, int receivableId) = await SeedOverdueCreditScenarioAsync(database);

        // Clase 4 con clave fija (4, 0): el barrido global se serializa contra sí mismo.
        await using var gate = new NpgsqlConnection(database.ConnectionString);
        await gate.OpenAsync();
        await using var gateTransaction = await gate.BeginTransactionAsync();
        await using (var gateCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(4, 0)", gate, gateTransaction))
        {
            await gateCommand.ExecuteScalarAsync();
        }

        var sweep = Task.Run(async () =>
        {
            await using var context = database.CreateSalesDbContext();
            return await new CreditService(context).EvaluateOverdueAndMoraAsync();
        });

        bool blockedOnClass4 = await WaitForUngrantedAdvisoryLockAsync(database.ConnectionString, lockClass: 4, lockKey: 0, TimeSpan.FromSeconds(20));
        Assert.True(blockedOnClass4, "El barrido de mora debe quedar esperando el advisory lock global (clase 4, clave 0) mientras otra sesión lo retiene.");

        await gateTransaction.RollbackAsync();

        var result = await sweep.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(1, result.OverdueMaterializedCount);
        Assert.Equal(1, result.SuspendedCount);

        await using var verifyContext = database.CreateSalesDbContext();
        var persistedReceivable = await verifyContext.Receivables.AsNoTracking().SingleAsync(receivable => receivable.Id == receivableId);
        Assert.Equal(ReceivableStatus.Vencida, persistedReceivable.Status);
        var persistedCustomer = await verifyContext.Customers.AsNoTracking().SingleAsync(customer => customer.Id == customerId);
        Assert.Equal(CreditStatus.SuspendedByMora, persistedCustomer.CreditStatus);
    }

    [Fact]
    public async Task ConcurrentMoraSweeps_SerializeMaterializeOnce_AndStayIdempotent()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(seedStandardSalesData: true);
        (int customerId, int receivableId) = await SeedOverdueCreditScenarioAsync(database);

        var results = await Task.WhenAll(
            Task.Run(() => RunMoraSweepAsync(database)),
            Task.Run(() => RunMoraSweepAsync(database)));

        // El lock serializa: la materialización ocurre UNA sola vez (la segunda corrida ya ve Vencida)
        // y ninguna corrida falla por conflicto xmin no resuelto.
        Assert.Equal(1, results.Sum(result => result.OverdueMaterializedCount));
        Assert.Equal(1, results.Sum(result => result.SuspendedCount));

        var repeat = await RunMoraSweepAsync(database);
        Assert.Equal(0, repeat.OverdueMaterializedCount);
        Assert.Equal(0, repeat.SuspendedCount);
        Assert.Equal(0, repeat.ReactivatedCount);

        await using var verifyContext = database.CreateSalesDbContext();
        Assert.Equal(ReceivableStatus.Vencida, (await verifyContext.Receivables.AsNoTracking().SingleAsync(receivable => receivable.Id == receivableId)).Status);
        Assert.Equal(CreditStatus.SuspendedByMora, (await verifyContext.Customers.AsNoTracking().SingleAsync(customer => customer.Id == customerId)).CreditStatus);
    }

    // ---------------------------------------------------------------- xmin

    [Fact]
    public async Task Receivable_StaleXmin_ThrowsDbUpdateConcurrencyExceptionAndFreshRetryWins()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(seedStandardSalesData: true);
        (int _, int receivableId, int _) = await SeedCreditScenarioAsync(database, balanceUsd: 100m, dueInDays: 10);

        await using var winnerContext = database.CreateSalesDbContext();
        await using var staleContext = database.CreateSalesDbContext();

        var winner = await winnerContext.Receivables.SingleAsync(receivable => receivable.Id == receivableId);
        var stale = await staleContext.Receivables.SingleAsync(receivable => receivable.Id == receivableId);

        winner.BalanceUSD = 40m;
        winner.Status = ReceivableStatus.Parcial;
        await winnerContext.SaveChangesAsync();

        stale.BalanceUSD = 70m;
        stale.Status = ReceivableStatus.Parcial;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleContext.SaveChangesAsync());

        // Resolución esperada del contrato (reintento con estado fresco): el segundo escritor recarga
        // y gana sin perder el cambio del primero de forma silenciosa.
        await using var retryContext = database.CreateSalesDbContext();
        var fresh = await retryContext.Receivables.SingleAsync(receivable => receivable.Id == receivableId);
        Assert.Equal(40m, fresh.BalanceUSD);
        fresh.BalanceUSD = 70m;
        await retryContext.SaveChangesAsync();

        await using var verifyContext = database.CreateSalesDbContext();
        Assert.Equal(70m, (await verifyContext.Receivables.AsNoTracking().SingleAsync(receivable => receivable.Id == receivableId)).BalanceUSD);
    }

    // ---------------------------------------------------------------- caja chica: nextval + USD 4dp

    [Fact]
    public async Task PettyCashVoucherSequence_Nextval_GeneratesUniqueCorrelativesWithoutIntervention()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(seedStandardSalesData: true);

        await using var context = database.CreateSalesDbContext();
        var drawer = new CashDrawerService(context);
        var pettyCash = CreatePettyCashService(context, drawer);
        var session = await drawer.OpenSessionAsync(1000m, 50m);

        var first = await pettyCash.RegisterAsync(
            BuildPettyCashRequest(PettyCashCurrency.BsS, 10m, "REF-VCC-001"),
            actingUserId: 1,
            actingRole: UserRole.Admin);
        var second = await pettyCash.RegisterAsync(
            BuildPettyCashRequest(PettyCashCurrency.BsS, 20m, "REF-VCC-002"),
            actingUserId: 1,
            actingRole: UserRole.Admin);

        Assert.Equal(1, first.VoucherNumber);
        Assert.Equal(2, second.VoucherNumber);

        // El DEFAULT de la columna consume la misma secuencia dedicada: un tercer correlativo
        // único sin pasar por el servicio (el correlativo no depende del max persistido).
        long rawVoucher;
        await using (var rawConnection = new NpgsqlConnection(database.ConnectionString))
        {
            await rawConnection.OpenAsync();
            await using var insert = new NpgsqlCommand(
                "INSERT INTO \"PettyCashExpenses\" (" +
                "\"SessionId\", \"Category\", \"Currency\", \"AmountBsS\", \"AmountUsd\", \"HistoricalRate\", " +
                "\"Concept\", \"Beneficiary\", \"ReferenceNumber\", \"CreatedByUserId\", \"CreatedAtUtc\", \"Status\") " +
                "VALUES (@sessionId, 0, 0, 10.00, 0.2000, 50.0000, 'Correlativo crudo', 'Crudo', 'REF-RAW', 1, NOW(), 0) " +
                "RETURNING \"VoucherNumber\"",
                rawConnection);
            insert.Parameters.AddWithValue("sessionId", session.Id);
            rawVoucher = (long)(await insert.ExecuteScalarAsync())!;
        }

        Assert.Equal(3, rawVoucher);
        Assert.True(await RelationExistsAsync(database, "petty_cash_voucher_seq"), "La secuencia dedicada petty_cash_voucher_seq debe existir tras las migraciones 8.188.");
    }

    [Fact]
    public async Task PettyCashUsdBucket_PreservesFourDecimalsThroughRegisterAndPhysicalBalance()
    {
        await using var database = await IsolatedPostgresDatabase.CreateAsync(seedStandardSalesData: true);

        await using var context = database.CreateSalesDbContext();
        var drawer = new CashDrawerService(context);
        var pettyCash = CreatePettyCashService(context, drawer);
        var session = await drawer.OpenSessionAsync(100m, 50m);

        int usdMethodId = await context.PaymentMethods
            .Where(method => method.Name == "Efectivo USD")
            .Select(method => method.Id)
            .SingleAsync();

        // Fondo físico USD con 4dp: el inbound también debe conservar la escala.
        await drawer.AddTransactionAsync(
            session.Id,
            CashTransactionType.Income,
            CashTransactionSource.CashIn,
            1028.39m,
            20.5678m,
            50m,
            "Fondo USD de caja chica",
            isPhysicalCash: true,
            paymentMethodId: usdMethodId);

        var expense = await pettyCash.RegisterAsync(
            BuildPettyCashRequest(PettyCashCurrency.Usd, 10.1234m, "REF-USD-4DP"),
            actingUserId: 1,
            actingRole: UserRole.Admin);

        Assert.Equal(10.1234m, expense.AmountUsd);
        Assert.Equal(506.17m, expense.AmountBsS);

        await using var verifyContext = database.CreateSalesDbContext();
        var movement = await verifyContext.CashTransactions
            .AsNoTracking()
            .SingleAsync(transaction => transaction.Source == CashTransactionSource.PettyCashExpense);
        Assert.Equal(10.1234m, movement.AmountUsd);

        var physicalUsd = await new CashDrawerService(verifyContext)
            .GetPhysicalBalanceAsync(session.Id, PettyCashCurrency.Usd);
        Assert.Equal(10.4444m, physicalUsd);
    }

    // ---------------------------------------------------------------- harness

    private static SalesService CreateSalesService(SalesDbContext context, decimal bcvRate)
    {
        var inventory = new Mock<IInventoryService>();
        inventory
            .Setup(service => service.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(bcvRate);

        // Los abonos del carril usan métodos NO efectivo: la gaveta no interviene.
        return new SalesService(
            context,
            inventory.Object,
            new Mock<IMediator>().Object,
            new Mock<ICashDrawerService>().Object,
            new Mock<ISystemSettingsService>().Object,
            creditService: new CreditService(context));
    }

    private static PettyCashService CreatePettyCashService(SalesDbContext context, CashDrawerService drawer)
    {
        var inventory = new Mock<IInventoryService>();
        inventory
            .Setup(service => service.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(50m);

        return new PettyCashService(context, drawer, inventory.Object, new Mock<ISystemSettingsService>().Object);
    }

    private static PettyCashExpenseRequest BuildPettyCashRequest(PettyCashCurrency currency, decimal amount, string reference)
        => new()
        {
            Currency = currency,
            Amount = amount,
            Category = PettyCashCategory.InsumosLimpieza,
            Concept = "Gasto de carril Postgres",
            Beneficiary = "Proveedor carril Postgres",
            ReferenceNumber = reference
        };

    private static async Task<(int CustomerId, int ReceivableId, int PaymentMethodId)> SeedCreditScenarioAsync(
        IsolatedPostgresDatabase database,
        decimal balanceUsd,
        int dueInDays)
    {
        await using var context = database.CreateSalesDbContext();

        var customer = new Customer
        {
            CedulaOrRif = "V-" + Guid.NewGuid().ToString("N")[..8],
            Name = "Cliente carril Postgres",
            IsCreditEnabled = true,
            CreditLimitUSD = 5000m,
            PaymentTermDays = 30,
            CreditGraceDays = 0,
            CreditStatus = CreditStatus.Active,
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

        var receivable = new Receivable
        {
            SaleId = sale.Id,
            CustomerId = customer.Id,
            OriginalUSD = balanceUsd,
            BalanceUSD = balanceUsd,
            OriginalBsS = Math.Round(balanceUsd * 50m, 2, MidpointRounding.AwayFromZero),
            HistoricalRate = 50m,
            IssuedAtUtc = DateTime.UtcNow.AddDays(-1),
            DueDateUtc = DateTime.UtcNow.AddDays(dueInDays),
            Status = ReceivableStatus.Vigente
        };
        context.Receivables.Add(receivable);
        await context.SaveChangesAsync();

        int paymentMethodId = await context.PaymentMethods
            .Where(method => !method.IsCash && method.IsActive)
            .Select(method => method.Id)
            .FirstAsync();

        return (customer.Id, receivable.Id, paymentMethodId);
    }

    private static async Task<(int CustomerId, int ReceivableId)> SeedOverdueCreditScenarioAsync(IsolatedPostgresDatabase database)
    {
        (int customerId, int receivableId, int _) = await SeedCreditScenarioAsync(database, balanceUsd: 50m, dueInDays: -10);
        return (customerId, receivableId);
    }

    private static async Task<Exception?> TryRegisterPaymentAsync(IsolatedPostgresDatabase database, CreditPaymentRequest request)
    {
        try
        {
            await using var context = database.CreateSalesDbContext();
            await CreateSalesService(context, TestBcvRate).RegisterCreditPaymentAsync(request);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task<CreditMoraEvaluationResultDto> RunMoraSweepAsync(IsolatedPostgresDatabase database)
    {
        await using var context = database.CreateSalesDbContext();
        return await new CreditService(context).EvaluateOverdueAndMoraAsync();
    }

    /// <summary>
    /// Espera a que aparezca una solicitud de advisory lock NO concedida con la clave exacta
    /// (classid/objid, forma de dos argumentos int4). Es la prueba directa de que el servicio
    /// está bloqueado en ESA clase/clave y no en otra cosa.
    /// </summary>
    private static async Task<bool> WaitForUngrantedAdvisoryLockAsync(
        string connectionString,
        int lockClass,
        int lockKey,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand(
                "SELECT COUNT(*) FROM pg_locks " +
                "WHERE locktype = 'advisory' AND NOT granted AND objsubid = 2 " +
                "AND classid::bigint = @class AND objid::bigint = @key",
                connection);
            command.Parameters.AddWithValue("class", (long)lockClass);
            command.Parameters.AddWithValue("key", (long)lockKey);

            long waiting = (long)(await command.ExecuteScalarAsync())!;
            if (waiting > 0)
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }

    /// <summary>to_regclass exige el nombre tal como vive en pg_class: las tablas PascalCase van entre comillas.</summary>
    private static async Task<bool> RelationExistsAsync(IsolatedPostgresDatabase database, string relation)
    {
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT to_regclass(@relation) IS NOT NULL", connection);
        command.Parameters.AddWithValue("relation", relation);
        return (bool)(await command.ExecuteScalarAsync())!;
    }
}
