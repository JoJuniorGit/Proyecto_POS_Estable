using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.Entities;
using Core.Interfaces;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// T2a (Paso 13 / 8.188, S1/S2/S3/S5): registro bimonetario del gasto de caja chica, liquidez
/// fisica por moneda, idempotencia del gasto, anulacion compensatoria con snapshot inmutable y
/// consultas del turno. Corre con SQLite relacional (el advisory lock se omite en proveedores
/// no-Npgsql, igual que el resto de los servicios de caja).
/// </summary>
public class PettyCashServiceTests
{
    // Tasa BCV de prueba: el servicio la congela con techo a 2dp (convencion de caja).
    private const decimal Rate = 36.55m;

    private sealed class PettyCashStack : IDisposable
    {
        private readonly SqliteConnection _connection;

        public SalesDbContext Context { get; }
        public CashDrawerService Drawer { get; }
        public PettyCashService Service { get; }
        public Mock<IInventoryService> Inventory { get; } = new();
        public Mock<ISystemSettingsService> Settings { get; } = new();

        public PettyCashStack(decimal rate = Rate, string? threshold = null)
        {
            (Context, _connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
            Drawer = new CashDrawerService(Context);
            Inventory.Setup(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(rate);
            Settings.Setup(s => s.GetSettingAsync(It.IsAny<string>())).ReturnsAsync(threshold);
            Service = new PettyCashService(Context, Drawer, Inventory.Object, Settings.Object);
        }

        public void Dispose()
        {
            Context.Dispose();
            _connection.Dispose();
        }
    }

    private static async Task SeedPaymentMethodsAsync(SalesDbContext db, bool withUsdCash = true)
    {
        // El seed del modelo crea "Cash" (Id=1) ya dolarizado por nombre historico; se converge
        // el metodo local y se agrega el de USD para poder atribuir la gaveta correcta.
        var localCash = await db.PaymentMethods.FirstAsync(method => method.Id == 1);
        localCash.Name = "Efectivo Bs.S";
        localCash.IsCash = true;
        localCash.IsActive = true;
        localCash.IsDeleted = false;

        if (withUsdCash)
        {
            db.PaymentMethods.Add(new PaymentMethod
            {
                Id = 11,
                Name = "Efectivo USD",
                IsCash = true,
                IsActive = true,
                DisplayOrder = 4
            });
        }

        db.PaymentMethods.Add(new PaymentMethod
        {
            Id = 12,
            Name = "Pago Movil",
            IsCash = false,
            IsActive = true,
            DisplayOrder = 5
        });

        await db.SaveChangesAsync();
    }

    private static async Task<CashDrawerSession> SeedOpenSessionAsync(SalesDbContext db, decimal openingLocal, decimal openingUsd = 0m)
    {
        var session = new CashDrawerSession
        {
            OpenedAt = DateTime.UtcNow,
            Status = CashDrawerStatus.Open,
            OpeningBalanceLocal = openingLocal,
            OpeningExchangeRate = Rate,
            OpeningBalanceUsd = openingUsd
        };
        db.CashDrawerSessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }

    /// <summary>
    /// H3 (8.188): usuarios para la resolucion de nombres del vale. El usuario #1 (Admin
    /// "Administrador") ya viene del seed del modelo; se agrega el Gerente #2 supervisor.
    /// </summary>
    private static async Task SeedUsersAsync(SalesDbContext db)
    {
        db.Users.Add(new User
        {
            Id = 2,
            Cedula = "V-00000002",
            Name = "Gerente Dos",
            Username = "gerente2",
            FullName = "Gerente Dos",
            Role = UserRole.Manager
        });
        await db.SaveChangesAsync();
    }

    private static PettyCashExpenseRequest Request(
        PettyCashCurrency currency = PettyCashCurrency.BsS,
        decimal amount = 365.50m,
        PettyCashCategory category = PettyCashCategory.InsumosLimpieza,
        string concept = "Compra de jabon y cloro",
        string beneficiary = "Limpieza Total C.A.",
        string reference = "FACT-0001",
        string? idempotencyKey = null) => new()
        {
            Currency = currency,
            Amount = amount,
            Category = category,
            Concept = concept,
            Beneficiary = beneficiary,
            ReferenceNumber = reference,
            IdempotencyKey = idempotencyKey
        };

    // ------------------------------------------------------------------ registro y valoracion dual

    [Fact]
    public async Task RegisterAsync_InBsS_ComputesDualValuationSnapshotAndExpenseMovement()
    {
        using var stack = new PettyCashStack();
        await SeedPaymentMethodsAsync(stack.Context);
        var session = await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m);

        var result = await stack.Service.RegisterAsync(Request(), actingUserId: 1, actingRole: UserRole.Admin);

        Assert.True(result.Id > 0);
        Assert.Equal(session.Id, result.SessionId);
        Assert.Equal(PettyCashCurrency.BsS, result.Currency);
        Assert.Equal(PettyCashCategory.InsumosLimpieza, result.Category);
        Assert.Equal(365.50m, result.AmountBsS);
        Assert.Equal(10.0000m, result.AmountUsd);
        Assert.Equal(Rate, result.HistoricalRate);
        Assert.Equal(PettyCashStatus.Active, result.Status);
        Assert.Equal(1L, result.VoucherNumber);
        Assert.Equal(1, result.CreatedByUserId);
        // Quick win 8.188: sin supervisor distinto no se registra autorizador (antes se
        // auto-vinculaba el ejecutor y el vale mostraba su propio nombre).
        Assert.Null(result.AuthorizedByUserId);
        Assert.False(result.IsReplay);

        var expense = await stack.Context.PettyCashExpenses.AsNoTracking().SingleAsync();
        Assert.Null(expense.AuthorizedByUserId);
        Assert.Equal("Compra de jabon y cloro", expense.Concept);
        Assert.Equal("Limpieza Total C.A.", expense.Beneficiary);
        Assert.Equal("FACT-0001", expense.ReferenceNumber);
        Assert.Equal(PettyCashCategory.InsumosLimpieza, expense.Category);

        var movement = await stack.Context.CashTransactions.AsNoTracking()
            .SingleAsync(candidate => candidate.Source == CashTransactionSource.PettyCashExpense);
        Assert.Equal(CashTransactionType.Expense, movement.Type);
        Assert.True(movement.IsPhysicalCash);
        Assert.Equal(365.50m, movement.AmountLocal);
        Assert.Equal(10.0000m, movement.AmountUsd);
        Assert.Equal(Rate, movement.ExchangeRate);
        Assert.Equal(expense.Id, movement.PettyCashExpenseId);
        Assert.Equal(1, movement.PaymentMethodId);
        Assert.Equal(result.CashTransactionId, movement.Id);

        // La gaveta Bs.S desconto el egreso; la de USD quedo intacta.
        Assert.Equal(1000m - 365.50m, await stack.Drawer.GetPhysicalBalanceAsync(session.Id, PettyCashCurrency.BsS));
        Assert.Equal(0m, await stack.Drawer.GetPhysicalBalanceAsync(session.Id, PettyCashCurrency.Usd));
    }

    [Fact]
    public async Task RegisterAsync_DirectByElevatedRole_LeavesAuthorizerNullAndVoucherShowsNotApplicable()
    {
        using var stack = new PettyCashStack();
        await SeedPaymentMethodsAsync(stack.Context);
        await SeedUsersAsync(stack.Context);
        await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m);

        // Quick win 8.188: sin umbral no existe supervisor distinto; el gasto queda sin
        // autorizador (misma regla H3 de la anulacion) y el vale muestra "N/A".
        var expense = await stack.Service.RegisterAsync(
            Request(amount: 10m),
            actingUserId: 1,
            actingRole: UserRole.Admin);

        Assert.Null(expense.AuthorizedByUserId);

        var persisted = await stack.Context.PettyCashExpenses.AsNoTracking().SingleAsync();
        Assert.Null(persisted.AuthorizedByUserId);

        var data = await stack.Service.GetVoucherDataAsync(expense.Id);
        Assert.NotNull(data);
        Assert.Equal("N/A", data!.Context.AuthorizerName);
    }

    [Fact]
    public async Task RegisterAsync_InBsS_PersistsFourDecimalAmountUsdInExpenseMovement()
    {
        using var stack = new PettyCashStack();
        await SeedPaymentMethodsAsync(stack.Context);
        await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m);

        // 365.55 / 36.55 = 10.001367... -> 10.0014 con la convencion 4dp: el libro transaccional
        // debe conservar el cuarto decimal (H4 del cierre 8.188; la columna era 18,2 en Postgres).
        var expense = await stack.Service.RegisterAsync(
            Request(amount: 365.55m),
            actingUserId: 1,
            actingRole: UserRole.Admin);

        Assert.Equal(10.0014m, expense.AmountUsd);

        var movement = await stack.Context.CashTransactions.AsNoTracking()
            .SingleAsync(candidate => candidate.Source == CashTransactionSource.PettyCashExpense);
        Assert.Equal(10.0014m, movement.AmountUsd);
        Assert.Equal(expense.AmountUsd, movement.AmountUsd);
    }

    [Fact]
    public async Task RegisterAsync_InUsd_ComputesDualValuationAndUsesUsdBucket()
    {
        using var stack = new PettyCashStack();
        await SeedPaymentMethodsAsync(stack.Context);
        var session = await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m, openingUsd: 50m);

        var result = await stack.Service.RegisterAsync(
            Request(currency: PettyCashCurrency.Usd, amount: 20.00m, category: PettyCashCategory.FletesAcarreo),
            actingUserId: 1,
            actingRole: UserRole.Admin);

        Assert.Equal(20.0000m, result.AmountUsd);
        Assert.Equal(731.00m, result.AmountBsS);
        Assert.Equal(Rate, result.HistoricalRate);

        var movement = await stack.Context.CashTransactions.AsNoTracking()
            .SingleAsync(candidate => candidate.Source == CashTransactionSource.PettyCashExpense);
        Assert.Equal(20.0000m, movement.AmountUsd);
        Assert.Equal(731.00m, movement.AmountLocal);
        Assert.Equal(11, movement.PaymentMethodId);

        Assert.Equal(50m - 20m, await stack.Drawer.GetPhysicalBalanceAsync(session.Id, PettyCashCurrency.Usd));
        Assert.Equal(1000m, await stack.Drawer.GetPhysicalBalanceAsync(session.Id, PettyCashCurrency.BsS));
    }

    [Fact]
    public async Task RegisterAsync_InUsd_KeepsFourDecimalAmountInMovementAndPhysicalBucket()
    {
        using var stack = new PettyCashStack();
        await SeedPaymentMethodsAsync(stack.Context);
        var session = await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m, openingUsd: 50m);

        // H4 (8.188): el nominal USD de 4dp viaja exacto al movimiento y al bucket fisico de la
        // gaveta (la columna (18,2) truncaba el 4to decimal en Postgres).
        var expense = await stack.Service.RegisterAsync(
            Request(currency: PettyCashCurrency.Usd, amount: 20.1234m),
            actingUserId: 1,
            actingRole: UserRole.Admin);

        Assert.Equal(20.1234m, expense.AmountUsd);

        var movement = await stack.Context.CashTransactions.AsNoTracking()
            .SingleAsync(candidate => candidate.Source == CashTransactionSource.PettyCashExpense);
        Assert.Equal(20.1234m, movement.AmountUsd);

        Assert.Equal(50m - 20.1234m, await stack.Drawer.GetPhysicalBalanceAsync(session.Id, PettyCashCurrency.Usd));
        Assert.Equal(1000m, await stack.Drawer.GetPhysicalBalanceAsync(session.Id, PettyCashCurrency.BsS));
    }

    [Fact]
    public async Task RegisterAsync_WithoutUsdCashMethod_ThrowsClearConfigurationError()
    {
        using var stack = new PettyCashStack();
        await SeedPaymentMethodsAsync(stack.Context, withUsdCash: false);
        await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m, openingUsd: 50m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => stack.Service.RegisterAsync(
            Request(currency: PettyCashCurrency.Usd, amount: 20.00m),
            actingUserId: 1,
            actingRole: UserRole.Admin));

        Assert.Contains("USD", ex.Message);
        Assert.Empty(await stack.Context.PettyCashExpenses.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task RegisterAsync_WithInsufficientPhysicalBalanceInBsS_IsRejected()
    {
        using var stack = new PettyCashStack();
        await SeedPaymentMethodsAsync(stack.Context);
        var session = await SeedOpenSessionAsync(stack.Context, openingLocal: 100m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => stack.Service.RegisterAsync(
            Request(amount: 150m),
            actingUserId: 1,
            actingRole: UserRole.Admin));

        Assert.Contains("insuficiente", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await stack.Context.PettyCashExpenses.AsNoTracking().ToListAsync());
        Assert.Equal(100m, await stack.Drawer.GetPhysicalBalanceAsync(session.Id, PettyCashCurrency.BsS));
    }

    [Fact]
    public async Task RegisterAsync_WithInsufficientPhysicalBalanceInUsd_IsRejected()
    {
        using var stack = new PettyCashStack();
        await SeedPaymentMethodsAsync(stack.Context);
        await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m, openingUsd: 5m);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => stack.Service.RegisterAsync(
            Request(currency: PettyCashCurrency.Usd, amount: 10m),
            actingUserId: 1,
            actingRole: UserRole.Admin));

        Assert.Contains("insuficiente", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await stack.Context.PettyCashExpenses.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task RegisterAsync_RejectsInvalidRequiredFields()
    {
        using var stack = new PettyCashStack();
        await SeedPaymentMethodsAsync(stack.Context);
        await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m);

        await Assert.ThrowsAsync<ArgumentException>(() => stack.Service.RegisterAsync(
            Request(concept: " "), actingUserId: 1, actingRole: UserRole.Admin));
        await Assert.ThrowsAsync<ArgumentException>(() => stack.Service.RegisterAsync(
            Request(beneficiary: " "), actingUserId: 1, actingRole: UserRole.Admin));
        await Assert.ThrowsAsync<ArgumentException>(() => stack.Service.RegisterAsync(
            Request(reference: " "), actingUserId: 1, actingRole: UserRole.Admin));
        await Assert.ThrowsAsync<ArgumentException>(() => stack.Service.RegisterAsync(
            Request(amount: 0m), actingUserId: 1, actingRole: UserRole.Admin));
        await Assert.ThrowsAsync<ArgumentException>(() => stack.Service.RegisterAsync(
            Request(amount: -1m), actingUserId: 1, actingRole: UserRole.Admin));
        await Assert.ThrowsAsync<ArgumentException>(() => stack.Service.RegisterAsync(
            Request(idempotencyKey: new string('k', 101)), actingUserId: 1, actingRole: UserRole.Admin));
        await Assert.ThrowsAsync<InvalidOperationException>(() => stack.Service.RegisterAsync(
            Request(), actingUserId: null, actingRole: UserRole.Admin));

        Assert.Empty(await stack.Context.PettyCashExpenses.AsNoTracking().ToListAsync());
    }

    [Fact]
    public async Task RegisterAsync_WithSameIdempotencyKey_ReplaysWithoutDoubleMovement()
    {
        using var stack = new PettyCashStack();
        await SeedPaymentMethodsAsync(stack.Context);
        var session = await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m);

        var request = Request(idempotencyKey: "pc-key-1");
        var first = await stack.Service.RegisterAsync(request, actingUserId: 1, actingRole: UserRole.Admin);
        var replay = await stack.Service.RegisterAsync(request, actingUserId: 1, actingRole: UserRole.Admin);

        Assert.False(first.IsReplay);
        Assert.True(replay.IsReplay);
        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(first.VoucherNumber, replay.VoucherNumber);
        Assert.Equal(first.AmountBsS, replay.AmountBsS);
        Assert.Equal(first.AmountUsd, replay.AmountUsd);
        Assert.Equal(first.CashTransactionId, replay.CashTransactionId);

        Assert.Equal(1, await stack.Context.PettyCashExpenses.AsNoTracking().CountAsync());
        Assert.Equal(1, await stack.Context.CashTransactions.AsNoTracking()
            .CountAsync(candidate => candidate.Source == CashTransactionSource.PettyCashExpense));
        Assert.Equal(1000m - 365.50m, await stack.Drawer.GetPhysicalBalanceAsync(session.Id, PettyCashCurrency.BsS));
    }

    [Fact]
    public async Task RegisterAsync_ConsecutiveVouchers_AreSequential()
    {
        using var stack = new PettyCashStack();
        await SeedPaymentMethodsAsync(stack.Context);
        await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m);

        var first = await stack.Service.RegisterAsync(Request(amount: 10m), actingUserId: 1, actingRole: UserRole.Admin);
        var second = await stack.Service.RegisterAsync(Request(amount: 20m), actingUserId: 1, actingRole: UserRole.Admin);

        Assert.Equal(1L, first.VoucherNumber);
        Assert.Equal(2L, second.VoucherNumber);
    }

    // ------------------------------------------------------------------ saldo fisico por moneda

    [Fact]
    public async Task GetPhysicalBalanceAsync_BucketsByPaymentMethodCurrencyAndPhysicalCash()
    {
        using var stack = new PettyCashStack();
        await SeedPaymentMethodsAsync(stack.Context);
        var session = await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m);

        stack.Context.CashTransactions.AddRange(
            new CashTransaction { SessionId = session.Id, Type = CashTransactionType.Income, Source = CashTransactionSource.SalePayment, AmountLocal = 500m, AmountUsd = 13.68m, ExchangeRate = Rate, IsPhysicalCash = true, PaymentMethodId = 1, Description = "Venta efectivo Bs.S" },
            new CashTransaction { SessionId = session.Id, Type = CashTransactionType.Income, Source = CashTransactionSource.SalePayment, AmountLocal = 731m, AmountUsd = 20m, ExchangeRate = Rate, IsPhysicalCash = true, PaymentMethodId = 11, Description = "Venta efectivo USD" },
            new CashTransaction { SessionId = session.Id, Type = CashTransactionType.Expense, Source = CashTransactionSource.SalePayment, AmountLocal = 100m, AmountUsd = 2.74m, ExchangeRate = Rate, IsPhysicalCash = true, PaymentMethodId = 1, Description = "Vuelto" },
            new CashTransaction { SessionId = session.Id, Type = CashTransactionType.Expense, Source = CashTransactionSource.CashOut, AmountLocal = 50m, AmountUsd = 1.37m, ExchangeRate = Rate, IsPhysicalCash = true, PaymentMethodId = null, Description = "Salida sin metodo: bucket Bs.S" },
            new CashTransaction { SessionId = session.Id, Type = CashTransactionType.Income, Source = CashTransactionSource.SalePayment, AmountLocal = 9999m, AmountUsd = 273.5m, ExchangeRate = Rate, IsPhysicalCash = false, PaymentMethodId = 12, Description = "Pago movil no fisico" });
        await stack.Context.SaveChangesAsync();

        Assert.Equal(1000m + 500m - 100m - 50m, await stack.Drawer.GetPhysicalBalanceAsync(session.Id, PettyCashCurrency.BsS));
        Assert.Equal(20m, await stack.Drawer.GetPhysicalBalanceAsync(session.Id, PettyCashCurrency.Usd));
    }

    [Fact]
    public async Task GetPhysicalBalanceAsync_ReviewsExpenseAndReversalMovementsPerCurrency()
    {
        using var stack = new PettyCashStack();
        await SeedPaymentMethodsAsync(stack.Context);
        var session = await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m, openingUsd: 50m);

        var expense = await stack.Service.RegisterAsync(
            Request(currency: PettyCashCurrency.Usd, amount: 20m),
            actingUserId: 1,
            actingRole: UserRole.Admin);
        Assert.Equal(30m, await stack.Drawer.GetPhysicalBalanceAsync(session.Id, PettyCashCurrency.Usd));

        await stack.Service.CancelAsync(expense.Id, "Proveedor no entrego el pedido", actingUserId: 1, actingRole: UserRole.Admin);

        Assert.Equal(50m, await stack.Drawer.GetPhysicalBalanceAsync(session.Id, PettyCashCurrency.Usd));
        Assert.Equal(1000m, await stack.Drawer.GetPhysicalBalanceAsync(session.Id, PettyCashCurrency.BsS));
    }

    [Fact]
    public async Task GetPhysicalBalanceAsync_UnknownSession_ReturnsZero()
    {
        using var stack = new PettyCashStack();

        Assert.Equal(0m, await stack.Drawer.GetPhysicalBalanceAsync(9999, PettyCashCurrency.BsS));
        Assert.Equal(0m, await stack.Drawer.GetPhysicalBalanceAsync(9999, PettyCashCurrency.Usd));
    }

    // ------------------------------------------------------------------ anulacion compensatoria

    [Fact]
    public async Task CancelAsync_ReversesExactlyWithSnapshotAndKeepsHistory()
    {
        using var stack = new PettyCashStack();
        await SeedPaymentMethodsAsync(stack.Context);
        var session = await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m);

        var expense = await stack.Service.RegisterAsync(Request(amount: 100m), actingUserId: 1, actingRole: UserRole.Admin);
        var cancelledAt = DateTime.UtcNow;

        var cancelled = await stack.Service.CancelAsync(
            expense.Id,
            "Pedido devuelto al proveedor",
            actingUserId: 1,
            actingRole: UserRole.Admin);

        Assert.Equal(PettyCashStatus.Cancelled, cancelled.Status);
        Assert.Equal(1, cancelled.CancelledByUserId);
        Assert.NotNull(cancelled.CancelledAtUtc);
        Assert.True(cancelled.CancelledAtUtc >= cancelledAt.AddSeconds(-5));
        Assert.Equal("Pedido devuelto al proveedor", cancelled.CancellationReason);

        var reversal = await stack.Context.CashTransactions.AsNoTracking()
            .SingleAsync(candidate => candidate.Source == CashTransactionSource.PettyCashReversal);
        Assert.Equal(CashTransactionType.Income, reversal.Type);
        Assert.True(reversal.IsPhysicalCash);
        Assert.Equal(100m, reversal.AmountLocal);
        Assert.Equal(expense.AmountUsd, reversal.AmountUsd);
        Assert.Equal(Rate, reversal.ExchangeRate);
        Assert.Equal(expense.Id, reversal.PettyCashExpenseId);
        Assert.Equal(1, reversal.PaymentMethodId);

        // Efecto neto 0 en la gaveta y sin borrado fisico del gasto ni de su egreso original.
        Assert.Equal(1000m, await stack.Drawer.GetPhysicalBalanceAsync(session.Id, PettyCashCurrency.BsS));
        Assert.Equal(1, await stack.Context.PettyCashExpenses.AsNoTracking().CountAsync());
        Assert.Equal(1, await stack.Context.CashTransactions.AsNoTracking()
            .CountAsync(candidate => candidate.Source == CashTransactionSource.PettyCashExpense));
    }

    [Fact]
    public async Task CancelAsync_WithUsdExpense_ReversesWithFrozenSnapshots()
    {
        using var stack = new PettyCashStack();
        await SeedPaymentMethodsAsync(stack.Context);
        var session = await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m, openingUsd: 50m);

        var expense = await stack.Service.RegisterAsync(
            Request(currency: PettyCashCurrency.Usd, amount: 20m),
            actingUserId: 1,
            actingRole: UserRole.Admin);

        // La tasa vigente cambia despues del registro: la reversa JAMAS debe releerla.
        stack.Inventory.Setup(i => i.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>())).ReturnsAsync(99.99m);

        var cancelled = await stack.Service.CancelAsync(expense.Id, "Gasto duplicado", actingUserId: 1, actingRole: UserRole.Admin);

        Assert.Equal(expense.AmountBsS, cancelled.AmountBsS);
        Assert.Equal(expense.AmountUsd, cancelled.AmountUsd);
        Assert.Equal(expense.HistoricalRate, cancelled.HistoricalRate);

        var reversal = await stack.Context.CashTransactions.AsNoTracking()
            .SingleAsync(candidate => candidate.Source == CashTransactionSource.PettyCashReversal);
        Assert.Equal(expense.AmountUsd, reversal.AmountUsd);
        Assert.Equal(expense.AmountBsS, reversal.AmountLocal);
        Assert.Equal(expense.HistoricalRate, reversal.ExchangeRate);
        Assert.Equal(50m, await stack.Drawer.GetPhysicalBalanceAsync(session.Id, PettyCashCurrency.Usd));
    }

    [Fact]
    public async Task CancelAsync_WithSupervisorAuthorization_LinksApproverToExpenseAndVoucher()
    {
        using var stack = new PettyCashStack();
        await SeedPaymentMethodsAsync(stack.Context);
        await SeedUsersAsync(stack.Context);
        await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m);

        var expense = await stack.Service.RegisterAsync(Request(amount: 100m), actingUserId: 1, actingRole: UserRole.Admin);

        // Gerente ejecuta la anulacion; el Admin #1 la aprueba (token consumido por el controlador).
        var cancelled = await stack.Service.CancelAsync(
            expense.Id,
            "Proveedor no entrego el pedido",
            actingUserId: 2,
            actingRole: UserRole.Manager,
            authorizedByUserId: 1);

        Assert.Equal(PettyCashStatus.Cancelled, cancelled.Status);
        Assert.Equal(2, cancelled.CancelledByUserId);

        var persisted = await stack.Context.PettyCashExpenses.AsNoTracking().SingleAsync();
        Assert.Equal(1, persisted.CancellationAuthorizedByUserId);

        // El vale resuelve ambos nombres: quien anulo y quien aprobo (H3 del cierre 8.188).
        var data = await stack.Service.GetVoucherDataAsync(expense.Id);
        Assert.NotNull(data);
        Assert.Equal("Gerente Dos", data!.Context.CancelledByName);
        Assert.Equal("Administrador", data.Context.CancellationAuthorizedName);
    }

    [Fact]
    public async Task CancelAsync_AdminDirect_LeavesCancellationAuthorizerNull()
    {
        using var stack = new PettyCashStack();
        await SeedPaymentMethodsAsync(stack.Context);
        await SeedUsersAsync(stack.Context);
        await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m);

        var expense = await stack.Service.RegisterAsync(Request(amount: 100m), actingUserId: 1, actingRole: UserRole.Admin);

        // Admin anula directo: no hay supervisor distinto que registrar.
        var cancelled = await stack.Service.CancelAsync(
            expense.Id,
            "Gasto duplicado",
            actingUserId: 1,
            actingRole: UserRole.Admin);

        Assert.Equal(PettyCashStatus.Cancelled, cancelled.Status);

        var persisted = await stack.Context.PettyCashExpenses.AsNoTracking().SingleAsync();
        Assert.Null(persisted.CancellationAuthorizedByUserId);

        var data = await stack.Service.GetVoucherDataAsync(expense.Id);
        Assert.NotNull(data);
        Assert.Null(data!.Context.CancellationAuthorizedName);
    }

    [Fact]
    public async Task CancelAsync_WhenApproverEqualsActingUser_IsNotStoredAsSupervisor()
    {
        using var stack = new PettyCashStack();
        await SeedPaymentMethodsAsync(stack.Context);
        await SeedUsersAsync(stack.Context);
        await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m);

        var expense = await stack.Service.RegisterAsync(Request(amount: 100m), actingUserId: 1, actingRole: UserRole.Admin);

        // Caso limite: el aprobador declarado es el propio ejecutor -> no existe supervisor
        // distinto y la trazabilidad queda en null (misma regla de Admin directo).
        await stack.Service.CancelAsync(
            expense.Id,
            "Gasto duplicado",
            actingUserId: 2,
            actingRole: UserRole.Manager,
            authorizedByUserId: 2);

        var persisted = await stack.Context.PettyCashExpenses.AsNoTracking().SingleAsync();
        Assert.Null(persisted.CancellationAuthorizedByUserId);
    }

    [Fact]
    public async Task CancelAsync_Twice_SecondIsRejectedAsDomainConflict()
    {
        using var stack = new PettyCashStack();
        await SeedPaymentMethodsAsync(stack.Context);
        await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m);

        var expense = await stack.Service.RegisterAsync(Request(amount: 100m), actingUserId: 1, actingRole: UserRole.Admin);
        await stack.Service.CancelAsync(expense.Id, "Primera anulacion", actingUserId: 1, actingRole: UserRole.Admin);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => stack.Service.CancelAsync(
            expense.Id,
            "Segunda anulacion",
            actingUserId: 1,
            actingRole: UserRole.Admin));

        Assert.Contains("anulado", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await stack.Context.CashTransactions.AsNoTracking()
            .CountAsync(candidate => candidate.Source == CashTransactionSource.PettyCashReversal));
    }

    [Fact]
    public async Task CancelAsync_ClosedSession_IsRejected()
    {
        using var stack = new PettyCashStack();
        await SeedPaymentMethodsAsync(stack.Context);
        await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m);

        var expense = await stack.Service.RegisterAsync(Request(amount: 100m), actingUserId: 1, actingRole: UserRole.Admin);

        // Cierre del turno directo sobre la fila: aísla la regla de anulación (la sesión cerrada
        // rechaza la reversa) del advisory lock Npgsql del cierre real, que no existe en SQLite.
        var trackedSession = await stack.Context.CashDrawerSessions.SingleAsync();
        trackedSession.Status = CashDrawerStatus.Closed;
        trackedSession.ClosedAt = DateTime.UtcNow;
        await stack.Context.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => stack.Service.CancelAsync(
            expense.Id,
            "Intento tardio",
            actingUserId: 1,
            actingRole: UserRole.Admin));

        Assert.Contains("cerrada", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CancelAsync_WithoutReason_IsRejectedAndDoesNotMutate()
    {
        using var stack = new PettyCashStack();
        await SeedPaymentMethodsAsync(stack.Context);
        await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m);

        var expense = await stack.Service.RegisterAsync(Request(amount: 100m), actingUserId: 1, actingRole: UserRole.Admin);

        await Assert.ThrowsAsync<ArgumentException>(() => stack.Service.CancelAsync(expense.Id, "  ", actingUserId: 1, actingRole: UserRole.Admin));
        await Assert.ThrowsAsync<ArgumentException>(() => stack.Service.CancelAsync(expense.Id, new string('r', 501), actingUserId: 1, actingRole: UserRole.Admin));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => stack.Service.CancelAsync(9999, "Motivo", actingUserId: 1, actingRole: UserRole.Admin));

        var persisted = await stack.Context.PettyCashExpenses.AsNoTracking().SingleAsync();
        Assert.Equal(PettyCashStatus.Active, persisted.Status);
        Assert.Equal(0, await stack.Context.CashTransactions.AsNoTracking()
            .CountAsync(candidate => candidate.Source == CashTransactionSource.PettyCashReversal));
    }

    // ------------------------------------------------------------------ consultas del turno

    [Fact]
    public async Task GetSessionExpensesAsync_ReturnsExpensesWithMovementLinkAndStatus()
    {
        using var stack = new PettyCashStack();
        await SeedPaymentMethodsAsync(stack.Context);
        var session = await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m);

        var first = await stack.Service.RegisterAsync(Request(amount: 10m), actingUserId: 1, actingRole: UserRole.Admin);
        var second = await stack.Service.RegisterAsync(Request(amount: 20m), actingUserId: 1, actingRole: UserRole.Admin);
        var cancelled = await stack.Service.CancelAsync(second.Id, "Anulado para prueba", actingUserId: 1, actingRole: UserRole.Admin);

        var expenses = await stack.Service.GetSessionExpensesAsync(session.Id);

        Assert.Equal(2, expenses.Count);
        Assert.Equal(first.Id, expenses[0].Id);
        Assert.Equal(second.Id, expenses[1].Id);
        Assert.Equal(PettyCashStatus.Active, expenses[0].Status);
        Assert.Equal(PettyCashStatus.Cancelled, expenses[1].Status);
        Assert.Equal(cancelled.CancellationReason, expenses[1].CancellationReason);
        Assert.NotNull(expenses[0].CashTransactionId);
        Assert.NotNull(expenses[1].CashTransactionId);
        Assert.Empty(await stack.Service.GetSessionExpensesAsync(9999));
    }

    [Fact]
    public async Task RegisterAsync_IdempotentReplay_DoesNotRequireLiveRate()
    {
        using var stack = new PettyCashStack();
        await SeedPaymentMethodsAsync(stack.Context);
        await SeedOpenSessionAsync(stack.Context, openingLocal: 1000m);

        var request = Request(idempotencyKey: "pc-replay-1");
        var first = await stack.Service.RegisterAsync(request, actingUserId: 1, actingRole: UserRole.Admin);

        // La tasa viva deja de estar disponible: el replay debe responder desde el snapshot
        // persistido, sin volver a valorar ni duplicar movimientos.
        stack.Inventory
            .Setup(inventory => inventory.GetTodayExchangeRateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("BCV no disponible"));

        var replay = await stack.Service.RegisterAsync(request, actingUserId: 1, actingRole: UserRole.Admin);

        Assert.True(replay.IsReplay);
        Assert.Equal(first.Id, replay.Id);
        Assert.Equal(first.HistoricalRate, replay.HistoricalRate);
        Assert.Equal(1, await stack.Context.PettyCashExpenses.AsNoTracking().CountAsync());
        Assert.Equal(1, await stack.Context.CashTransactions.AsNoTracking().CountAsync());
    }
}
