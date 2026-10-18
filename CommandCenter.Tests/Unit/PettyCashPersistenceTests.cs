using System;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Receipts;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// T1 (Paso 13 / 8.188, S2/S6): contrato de dominio y persistencia de la caja chica bimonetaria.
/// Cubre enums append-only, metadatos EF (precisiones, unique VoucherNumber, indices, FKs
/// Restrict), gaveta dual de la sesion (OpeningBalanceUsd/ClosingBalanceUsd), round-trip SQLite
/// del gasto con sus movimientos Expense/Reversal vinculados por PettyCashExpenseId, restricciones
/// de borrado y la migracion aditiva idempotente con guards `DO $$`.
/// </summary>
public class PettyCashPersistenceTests
{
    private static readonly DateTime FixedUtc = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PettyCashEnums_AppendValuesWithoutRenumberingExistingOnes()
    {
        // CashTransactionSource conserva 0..8 (Paso 11/12) y anexa el egreso y su reversa.
        Assert.Equal(0, (int)CashTransactionSource.Opening);
        Assert.Equal(1, (int)CashTransactionSource.SalePayment);
        Assert.Equal(2, (int)CashTransactionSource.CashAdvance);
        Assert.Equal(3, (int)CashTransactionSource.ManualAdjustment);
        Assert.Equal(4, (int)CashTransactionSource.Closing);
        Assert.Equal(5, (int)CashTransactionSource.CashIn);
        Assert.Equal(6, (int)CashTransactionSource.CashOut);
        Assert.Equal(7, (int)CashTransactionSource.CreditPayment);
        Assert.Equal(8, (int)CashTransactionSource.CreditRefund);
        Assert.Equal(9, (int)CashTransactionSource.PettyCashExpense);
        Assert.Equal(10, (int)CashTransactionSource.PettyCashReversal);

        // Accion de autorizacion jerarquica (S4) anexada al final.
        Assert.Equal(1, (int)Core.Entities.AuthorizationActionType.ManualPriceOverride);
        Assert.Equal(2, (int)Core.Entities.AuthorizationActionType.SaleCancellation);
        Assert.Equal(3, (int)Core.Entities.AuthorizationActionType.CreditOverLimit);
        Assert.Equal(4, (int)Core.Entities.AuthorizationActionType.PettyCashExpense);

        // Comprobante del vale de caja chica (S7).
        Assert.Equal(0, (int)ReceiptDocumentKind.NonFiscalSaleReceipt);
        Assert.Equal(1, (int)ReceiptDocumentKind.DeliveryNote);
        Assert.Equal(2, (int)ReceiptDocumentKind.CreditSale);
        Assert.Equal(3, (int)ReceiptDocumentKind.CreditPayment);
        Assert.Equal(4, (int)ReceiptDocumentKind.PettyCashVoucher);

        // Categorias operativas persistidas por id (S1): append-only.
        Assert.Equal(0, (int)PettyCashCategory.InsumosLimpieza);
        Assert.Equal(1, (int)PettyCashCategory.FletesAcarreo);
        Assert.Equal(2, (int)PettyCashCategory.MantenimientoReparaciones);
        Assert.Equal(3, (int)PettyCashCategory.ServiciosPublicos);
        Assert.Equal(4, (int)PettyCashCategory.Papeleria);
        Assert.Equal(5, (int)PettyCashCategory.Varios);

        Assert.Equal(0, (int)PettyCashCurrency.BsS);
        Assert.Equal(1, (int)PettyCashCurrency.Usd);

        Assert.Equal(0, (int)PettyCashStatus.Active);
        Assert.Equal(1, (int)PettyCashStatus.Cancelled);
    }

    [Fact]
    public void CashDrawerSession_DualDrawerDefaults_AreSafe()
    {
        // La gaveta dual arranca sin dolares: apertura en 0 y cierre sin contar (null).
        var session = new CashDrawerSession();

        Assert.Equal(0m, session.OpeningBalanceUsd);
        Assert.Null(session.ClosingBalanceUsd);
    }

    [Fact]
    public void SalesModel_MapsPettyCashExpenseContract()
    {
        using var context = CreateNpgsqlModelContext();
        var expense = context.Model.FindEntityType(typeof(PettyCashExpense));

        Assert.NotNull(expense);
        Assert.Equal("PettyCashExpenses", expense!.GetTableName());

        // Correlativo unico del vale provisto por la secuencia dedicada (VCC-000001).
        var voucher = expense.FindProperty(nameof(PettyCashExpense.VoucherNumber));
        Assert.NotNull(voucher);
        Assert.Equal(typeof(long), voucher!.ClrType);
        Assert.Equal("bigint", voucher.GetColumnType());
        Assert.Equal(ValueGenerated.OnAdd, voucher.ValueGenerated);
        Assert.Equal("nextval('petty_cash_voucher_seq')", voucher.GetDefaultValueSql());

        Assert.Contains(context.Model.GetSequences(), sequence => sequence.Name == "petty_cash_voucher_seq");

        // Precisiones monetarias: Bs.S 2dp; USD y tasa 4dp.
        Assert.Equal("numeric(18,2)", expense.FindProperty(nameof(PettyCashExpense.AmountBsS))!.GetColumnType());
        Assert.Equal("numeric(18,4)", expense.FindProperty(nameof(PettyCashExpense.AmountUsd))!.GetColumnType());
        Assert.Equal("numeric(18,4)", expense.FindProperty(nameof(PettyCashExpense.HistoricalRate))!.GetColumnType());

        // Tipificacion y trazabilidad administrativa (S1).
        Assert.Equal("integer", expense.FindProperty(nameof(PettyCashExpense.Category))!.GetColumnType());
        Assert.Equal("integer", expense.FindProperty(nameof(PettyCashExpense.Currency))!.GetColumnType());
        Assert.Equal("integer", expense.FindProperty(nameof(PettyCashExpense.Status))!.GetColumnType());
        Assert.Equal("timestamp with time zone", expense.FindProperty(nameof(PettyCashExpense.CreatedAtUtc))!.GetColumnType());

        var concept = expense.FindProperty(nameof(PettyCashExpense.Concept));
        Assert.NotNull(concept);
        Assert.Equal(500, concept!.GetMaxLength());
        Assert.False(concept.IsNullable);

        var beneficiary = expense.FindProperty(nameof(PettyCashExpense.Beneficiary));
        Assert.NotNull(beneficiary);
        Assert.Equal(150, beneficiary!.GetMaxLength());
        Assert.False(beneficiary.IsNullable);

        var reference = expense.FindProperty(nameof(PettyCashExpense.ReferenceNumber));
        Assert.NotNull(reference);
        Assert.Equal(100, reference!.GetMaxLength());
        Assert.False(reference.IsNullable);

        var cancellationReason = expense.FindProperty(nameof(PettyCashExpense.CancellationReason));
        Assert.NotNull(cancellationReason);
        Assert.Equal(500, cancellationReason!.GetMaxLength());
        Assert.True(cancellationReason.IsNullable);

        // Correlativo unico + indices de arqueo/cierre y de las FK.
        var voucherIndex = Assert.Single(
            expense.GetIndexes(),
            index => index.GetDatabaseName() == "IX_PettyCashExpenses_VoucherNumber");
        Assert.True(voucherIndex.IsUnique);

        // T2a: idempotencia del gasto con clave opcional y unicidad solo cuando existe.
        var idempotency = expense.FindProperty(nameof(PettyCashExpense.IdempotencyKey));
        Assert.NotNull(idempotency);
        Assert.Equal(typeof(string), idempotency!.ClrType);
        Assert.True(idempotency.IsNullable);
        Assert.Equal(100, idempotency.GetMaxLength());

        var idempotencyIndex = Assert.Single(
            expense.GetIndexes(),
            index => index.GetDatabaseName() == "IX_PettyCashExpenses_IdempotencyKey");
        Assert.True(idempotencyIndex.IsUnique);
        Assert.Equal("\"IdempotencyKey\" IS NOT NULL", idempotencyIndex.GetFilter());

        Assert.Contains(expense.GetIndexes(), index => index.GetDatabaseName() == "IX_PettyCashExpenses_SessionId");
        Assert.Contains(expense.GetIndexes(), index => index.GetDatabaseName() == "IX_PettyCashExpenses_SessionId_Status");
        Assert.Contains(expense.GetIndexes(), index => index.GetDatabaseName() == "IX_PettyCashExpenses_CreatedByUserId");
        Assert.Contains(expense.GetIndexes(), index => index.GetDatabaseName() == "IX_PettyCashExpenses_AuthorizedByUserId");
        Assert.Contains(expense.GetIndexes(), index => index.GetDatabaseName() == "IX_PettyCashExpenses_CancelledByUserId");
        Assert.Contains(expense.GetIndexes(), index => index.GetDatabaseName() == "IX_PettyCashExpenses_AuthorizationRequestId");

        // FK obligatoria y Restrict hacia la sesion; ninguna FK hacia CashTransaction:
        // el vinculo vive en CashTransaction.PettyCashExpenseId (sin ciclo).
        var sessionForeignKey = Assert.Single(
            expense.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(CashDrawerSession));
        Assert.Equal(DeleteBehavior.Restrict, sessionForeignKey.DeleteBehavior);
        Assert.True(sessionForeignKey.IsRequired);

        Assert.Equal(5, expense.GetForeignKeys().Count());
        Assert.All(expense.GetForeignKeys(), foreignKey => Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior));
        Assert.DoesNotContain(expense.GetForeignKeys(), foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(CashTransaction));

        var transactionsNavigation = expense.FindNavigation(nameof(PettyCashExpense.CashTransactions));
        Assert.NotNull(transactionsNavigation);
        Assert.True(transactionsNavigation!.IsCollection);
    }

    [Fact]
    public void SalesModel_MapsDualDrawerSessionColumns()
    {
        using var context = CreateNpgsqlModelContext();
        var session = context.Model.FindEntityType(typeof(CashDrawerSession));

        Assert.NotNull(session);

        var openingUsd = session!.FindProperty(nameof(CashDrawerSession.OpeningBalanceUsd));
        Assert.NotNull(openingUsd);
        Assert.Equal("numeric(18,4)", openingUsd!.GetColumnType());
        Assert.False(openingUsd.IsNullable);

        var closingUsd = session.FindProperty(nameof(CashDrawerSession.ClosingBalanceUsd));
        Assert.NotNull(closingUsd);
        Assert.Equal("numeric(18,4)", closingUsd!.GetColumnType());
        Assert.True(closingUsd.IsNullable);
    }

    [Fact]
    public void SalesModel_MapsCashTransactionPettyCashLink()
    {
        using var context = CreateNpgsqlModelContext();
        var transaction = context.Model.FindEntityType(typeof(CashTransaction));

        Assert.NotNull(transaction);

        var expenseId = transaction!.FindProperty(nameof(CashTransaction.PettyCashExpenseId));
        Assert.NotNull(expenseId);
        Assert.Equal(typeof(int?), expenseId!.ClrType);
        Assert.Equal("integer", expenseId.GetColumnType());
        Assert.True(expenseId.IsNullable);

        var foreignKey = Assert.Single(
            transaction.GetForeignKeys(),
            candidate => candidate.PrincipalEntityType.ClrType == typeof(PettyCashExpense));
        Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior);
        Assert.False(foreignKey.IsRequired);

        Assert.Contains(transaction.GetIndexes(), index => index.GetDatabaseName() == "IX_CashTransactions_PettyCashExpenseId");
        Assert.NotNull(transaction.FindNavigation(nameof(CashTransaction.PettyCashExpense)));
    }

    [Fact]
    public async Task PettyCashGraph_RoundTrips_OnSqlite()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            var (session, expense, _, _) = await SeedPettyCashGraphAsync(context);

            context.ChangeTracker.Clear();

            var persisted = await context.PettyCashExpenses
                .AsNoTracking()
                .Include(candidate => candidate.CashTransactions)
                .SingleAsync(candidate => candidate.Id == expense.Id);

            Assert.Equal(1L, persisted.VoucherNumber);
            Assert.Equal(session.Id, persisted.SessionId);
            Assert.Equal(PettyCashCategory.MantenimientoReparaciones, persisted.Category);
            Assert.Equal(PettyCashCurrency.Usd, persisted.Currency);
            Assert.Equal(3654.32m, persisted.AmountBsS);
            Assert.Equal(100.0000m, persisted.AmountUsd);
            Assert.Equal(36.5432m, persisted.HistoricalRate);
            Assert.Equal("Reparacion de vitrina principal", persisted.Concept);
            Assert.Equal("Ferreteria El Tornillo", persisted.Beneficiary);
            Assert.Equal("FACT-0001", persisted.ReferenceNumber);
            Assert.Equal("pc-graph-key", persisted.IdempotencyKey);
            Assert.Equal(1, persisted.CreatedByUserId);
            Assert.Equal(FixedUtc, persisted.CreatedAtUtc);
            Assert.Equal(1, persisted.AuthorizedByUserId);
            Assert.Null(persisted.AuthorizationRequestId);
            Assert.Equal(PettyCashStatus.Active, persisted.Status);
            Assert.Null(persisted.CancelledAtUtc);

            // El gasto y su reversa compensatoria apuntan al mismo gasto por la FK dedicada.
            Assert.Equal(2, persisted.CashTransactions.Count);

            var expenseMovement = Assert.Single(
                persisted.CashTransactions,
                candidate => candidate.Source == CashTransactionSource.PettyCashExpense);
            Assert.Equal(CashTransactionType.Expense, expenseMovement.Type);
            Assert.Equal(expense.Id, expenseMovement.PettyCashExpenseId);
            Assert.Equal(100.00m, expenseMovement.AmountUsd);
            Assert.Equal(3654.32m, expenseMovement.AmountLocal);
            Assert.Equal(36.5432m, expenseMovement.ExchangeRate);

            var reversalMovement = Assert.Single(
                persisted.CashTransactions,
                candidate => candidate.Source == CashTransactionSource.PettyCashReversal);
            Assert.Equal(CashTransactionType.Income, reversalMovement.Type);
            Assert.Equal(expense.Id, reversalMovement.PettyCashExpenseId);
            Assert.Equal(expenseMovement.AmountUsd, reversalMovement.AmountUsd);
            Assert.Equal(expenseMovement.AmountLocal, reversalMovement.AmountLocal);
            Assert.Equal(expenseMovement.ExchangeRate, reversalMovement.ExchangeRate);

            var persistedSession = await context.CashDrawerSessions
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == session.Id);

            Assert.Equal(20.0000m, persistedSession.OpeningBalanceUsd);
            Assert.Null(persistedSession.ClosingBalanceUsd);
        }
    }

    [Fact]
    public async Task DeletingSessionReferencedByPettyCashExpense_IsRestricted_OnSqlite()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            // La sesion referenciada por un gasto no puede borrarse (sin movimientos de caja
            // involucrados: aisla la FK Restrict de PettyCashExpenses.SessionId).
            var session = CreateOpenSession();
            var expense = CreateExpense(session);

            context.AddRange(session, expense);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var trackedSession = await context.CashDrawerSessions.SingleAsync(candidate => candidate.Id == session.Id);
            context.CashDrawerSessions.Remove(trackedSession);

            var rejected = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.NotNull(rejected.InnerException);
        }
    }

    [Fact]
    public async Task DeletingExpenseReferencedByCashTransaction_IsRestricted_OnSqlite()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            var (_, expense, _, _) = await SeedPettyCashGraphAsync(context);
            context.ChangeTracker.Clear();

            var trackedExpense = await context.PettyCashExpenses.SingleAsync(candidate => candidate.Id == expense.Id);
            context.PettyCashExpenses.Remove(trackedExpense);

            // El vinculo vive en CashTransaction.PettyCashExpenseId: la traza contable nunca
            // se borra fisicamente, la anulacion es logica y compensatoria (S5).
            var rejected = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.NotNull(rejected.InnerException);
        }
    }

    [Fact]
    public async Task PettyCashVoucherUniqueness_RejectsDuplicates_OnSqlite()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            var session = CreateOpenSession();
            var first = CreateExpense(session);
            first.VoucherNumber = 5;

            context.AddRange(session, first);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            // Mismo correlativo de vale: viola IX_PettyCashExpenses_VoucherNumber.
            var duplicate = CreateExpense(session);
            duplicate.VoucherNumber = 5;
            context.PettyCashExpenses.Add(duplicate);

            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            context.ChangeTracker.Clear();
        }
    }

    [Fact]
    public void Migration_AddPettyCashExpensesRev8188_IsAdditiveAndGuarded()
    {
        using var context = CreateNpgsqlModelContext();
        var migrations = context.GetService<IMigrationsAssembly>();

        var migrationKey = Assert.Single(migrations.Migrations.Keys, key => key.Contains("AddPettyCashExpensesRev8188"));

        // Posterior a la ultima migracion del repo (8.187-T3b2) — comparacion ordinal del prefijo.
        Assert.True(string.CompareOrdinal(migrationKey.Substring(0, 14), "20261018124312") > 0,
            $"El timestamp de {migrationKey} debe ser posterior a 20261018124312.");

        var migration = (Migration)Activator.CreateInstance(migrations.Migrations[migrationKey].AsType())!;
        Assert.Empty(migration.UpOperations.OfType<DropTableOperation>());
        Assert.Empty(migration.UpOperations.OfType<DropColumnOperation>());

        var script = context.GetService<IMigrator>().GenerateScript(
            fromMigration: "20261018124312_AddCreditReturnRev8187",
            toMigration: migrationKey);

        // Gaveta dual de la sesion con guards information_schema y defaults seguros.
        Assert.Contains("information_schema.columns", script);
        Assert.Contains("\"CashDrawerSessions\" ADD COLUMN \"OpeningBalanceUsd\"", script);
        Assert.Contains("numeric(18,4) NOT NULL DEFAULT 0", script);
        Assert.Contains("\"CashDrawerSessions\" ADD COLUMN \"ClosingBalanceUsd\"", script);

        // Secuencia dedicada del correlativo del vale.
        Assert.Contains("CREATE SEQUENCE IF NOT EXISTS \"petty_cash_voucher_seq\"", script);
        Assert.Contains("nextval('petty_cash_voucher_seq')", script);

        // Tabla nueva idempotente con sus indices y FKs Restrict.
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"PettyCashExpenses\"", script);
        Assert.Contains("IX_PettyCashExpenses_VoucherNumber", script);
        Assert.Contains("IX_PettyCashExpenses_SessionId", script);
        Assert.Contains("IX_PettyCashExpenses_SessionId_Status", script);
        Assert.Contains("IX_PettyCashExpenses_CreatedByUserId", script);
        Assert.Contains("IX_PettyCashExpenses_AuthorizationRequestId", script);
        Assert.Contains("CREATE UNIQUE INDEX", script);
        Assert.Contains("ON DELETE RESTRICT", script);

        // Vinculo del libro transaccional con el gasto, idempotente.
        Assert.Contains("ALTER TABLE \"CashTransactions\" ADD COLUMN IF NOT EXISTS \"PettyCashExpenseId\"", script);
        Assert.Contains("IX_CashTransactions_PettyCashExpenseId", script);

        Assert.Contains("DO $$", script);
        Assert.DoesNotContain("DROP TABLE", script);
        Assert.DoesNotContain("DROP COLUMN", script);
    }

    [Fact]
    public void Migration_AddPettyCashExpensesRev8188_DownIsMirrorAndSafe()
    {
        using var context = CreateNpgsqlModelContext();
        var migrations = context.GetService<IMigrationsAssembly>();

        var migrationKey = Assert.Single(migrations.Migrations.Keys, key => key.Contains("AddPettyCashExpensesRev8188"));
        var migration = (Migration)Activator.CreateInstance(migrations.Migrations[migrationKey].AsType())!;

        var downSql = string.Join(
            Environment.NewLine,
            migration.DownOperations.OfType<SqlOperation>().Select(operation => operation.Sql));

        Assert.Contains("DROP TABLE IF EXISTS \"PettyCashExpenses\"", downSql);
        Assert.Contains("DROP SEQUENCE IF EXISTS \"petty_cash_voucher_seq\"", downSql);
        Assert.Contains("DROP COLUMN IF EXISTS \"PettyCashExpenseId\"", downSql);
        Assert.Contains("DROP COLUMN IF EXISTS \"ClosingBalanceUsd\"", downSql);
        Assert.Contains("DROP COLUMN IF EXISTS \"OpeningBalanceUsd\"", downSql);

        // El Down jamas destruye tablas de las que la migracion no es dueña.
        Assert.DoesNotContain("DROP TABLE IF EXISTS \"CashTransactions\"", downSql);
        Assert.DoesNotContain("DROP TABLE IF EXISTS \"CashDrawerSessions\"", downSql);
    }

    [Fact]
    public async Task PettyCashIdempotencyKeyUniqueness_RejectsDuplicates_OnSqlite()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            var session = CreateOpenSession();
            var first = CreateExpense(session);
            first.IdempotencyKey = "pc-key-duplicada";

            context.AddRange(session, first);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            // Misma clave de idempotencia: viola IX_PettyCashExpenses_IdempotencyKey.
            var duplicate = CreateExpense(session);
            duplicate.VoucherNumber = 2;
            duplicate.IdempotencyKey = "pc-key-duplicada";
            context.PettyCashExpenses.Add(duplicate);

            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            context.ChangeTracker.Clear();
        }
    }

    [Fact]
    public void Migration_AddPettyCashIdempotencyRev8188_IsAdditiveAndGuarded()
    {
        using var context = CreateNpgsqlModelContext();
        var migrations = context.GetService<IMigrationsAssembly>();

        var migrationKey = Assert.Single(migrations.Migrations.Keys, key => key.Contains("AddPettyCashIdempotencyRev8188"));

        // Posterior a la migracion de caja chica de T1 — comparacion ordinal del prefijo.
        Assert.True(string.CompareOrdinal(migrationKey.Substring(0, 14), "20261018165357") > 0,
            $"El timestamp de {migrationKey} debe ser posterior a 20261018165357.");

        var migration = (Migration)Activator.CreateInstance(migrations.Migrations[migrationKey].AsType())!;
        Assert.Empty(migration.UpOperations.OfType<DropTableOperation>());
        Assert.Empty(migration.UpOperations.OfType<DropColumnOperation>());

        var script = context.GetService<IMigrator>().GenerateScript(
            fromMigration: "20261018165357_AddPettyCashExpensesRev8188",
            toMigration: migrationKey);

        // Columna idempotente y indice unico filtrado (solo claves presentes).
        Assert.Contains("ADD COLUMN IF NOT EXISTS \"IdempotencyKey\"", script);
        Assert.Contains("CREATE UNIQUE INDEX IF NOT EXISTS \"IX_PettyCashExpenses_IdempotencyKey\"", script);
        Assert.Contains("\"IdempotencyKey\" IS NOT NULL", script);
        Assert.Contains("DO $$", script);

        Assert.DoesNotContain("DROP TABLE", script);
        Assert.DoesNotContain("DROP COLUMN", script);
    }

    private static SalesDbContext CreateNpgsqlModelContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused")
            .Options;
        return new SalesDbContext(options);
    }

    private static CashDrawerSession CreateOpenSession() => new()
    {
        OpenedAt = FixedUtc,
        Status = CashDrawerStatus.Open,
        OpeningBalanceLocal = 1000.00m,
        OpeningExchangeRate = 36.5432m,
        OpeningBalanceUsd = 20.0000m
    };

    private static PettyCashExpense CreateExpense(CashDrawerSession session) => new()
    {
        VoucherNumber = 1,
        Session = session,
        Category = PettyCashCategory.MantenimientoReparaciones,
        Currency = PettyCashCurrency.Usd,
        AmountBsS = 3654.32m,
        AmountUsd = 100.0000m,
        HistoricalRate = 36.5432m,
        Concept = "Reparacion de vitrina principal",
        Beneficiary = "Ferreteria El Tornillo",
        ReferenceNumber = "FACT-0001",
        CreatedByUserId = 1,
        CreatedAtUtc = FixedUtc,
        AuthorizedByUserId = 1,
        Status = PettyCashStatus.Active
    };

    private static async Task<(CashDrawerSession Session, PettyCashExpense Expense, CashTransaction ExpenseMovement, CashTransaction ReversalMovement)>
        SeedPettyCashGraphAsync(SalesDbContext context)
    {
        var session = CreateOpenSession();
        var expense = CreateExpense(session);
        expense.IdempotencyKey = "pc-graph-key";

        // Movimiento de salida real y su reversa compensatoria (mismos snapshots, efecto neto 0).
        var expenseMovement = new CashTransaction
        {
            Session = session,
            TransactionTime = FixedUtc,
            Type = CashTransactionType.Expense,
            Source = CashTransactionSource.PettyCashExpense,
            AmountUsd = 100.00m,
            ExchangeRate = 36.5432m,
            AmountLocal = 3654.32m,
            Description = "Gasto de caja chica",
            PettyCashExpense = expense
        };

        var reversalMovement = new CashTransaction
        {
            Session = session,
            TransactionTime = FixedUtc.AddMinutes(5),
            Type = CashTransactionType.Income,
            Source = CashTransactionSource.PettyCashReversal,
            AmountUsd = 100.00m,
            ExchangeRate = 36.5432m,
            AmountLocal = 3654.32m,
            Description = "Reversa de caja chica",
            PettyCashExpense = expense
        };

        context.AddRange(session, expense, expenseMovement, reversalMovement);
        await context.SaveChangesAsync();

        return (session, expense, expenseMovement, reversalMovement);
    }
}
