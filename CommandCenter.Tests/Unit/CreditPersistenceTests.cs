using System;
using System.Linq;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.Entities;
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
/// F1 (Paso 11 + Paso 12): contrato de dominio y persistencia del credito CxC bimonetario.
/// Cubre enums append-only, metadatos EF (precisions, indices, FKs Restrict, xmin),
/// round-trip SQLite de la cadena cliente-venta-cuenta-abono-imputaciones y la migracion
/// aditiva con guards idempotentes.
/// </summary>
public class CreditPersistenceTests
{
    private static readonly DateTime FixedUtc = new(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void CreditEnums_AppendValuesWithoutRenumberingExistingOnes()
    {
        Assert.Equal(0, (int)CashTransactionSource.Opening);
        Assert.Equal(1, (int)CashTransactionSource.SalePayment);
        Assert.Equal(2, (int)CashTransactionSource.CashAdvance);
        Assert.Equal(3, (int)CashTransactionSource.ManualAdjustment);
        Assert.Equal(4, (int)CashTransactionSource.Closing);
        Assert.Equal(5, (int)CashTransactionSource.CashIn);
        Assert.Equal(6, (int)CashTransactionSource.CashOut);
        Assert.Equal(7, (int)CashTransactionSource.CreditPayment);
        Assert.Equal(8, (int)CashTransactionSource.CreditRefund);

        Assert.Equal(1, (int)AuthorizationActionType.ManualPriceOverride);
        Assert.Equal(2, (int)AuthorizationActionType.SaleCancellation);
        Assert.Equal(3, (int)AuthorizationActionType.CreditOverLimit);

        Assert.Equal(0, (int)ReceiptDocumentKind.NonFiscalSaleReceipt);
        Assert.Equal(1, (int)ReceiptDocumentKind.DeliveryNote);
        Assert.Equal(2, (int)ReceiptDocumentKind.CreditSale);
        Assert.Equal(3, (int)ReceiptDocumentKind.CreditPayment);

        Assert.Equal(0, (int)ReceivableStatus.Vigente);
        Assert.Equal(1, (int)ReceivableStatus.Parcial);
        Assert.Equal(2, (int)ReceivableStatus.Vencida);
        Assert.Equal(3, (int)ReceivableStatus.Pagada);
        Assert.Equal(4, (int)ReceivableStatus.Anulada);
    }

    [Fact]
    public void CustomerCreditProfile_DefaultsAreSafe()
    {
        var customer = new Customer();

        Assert.False(customer.IsCreditEnabled);
        Assert.Equal(30, customer.PaymentTermDays);
        Assert.Equal(0, customer.CreditGraceDays);
        Assert.Equal(CreditStatus.Active, customer.CreditStatus);
        Assert.Null(customer.CreditNotes);
    }

    [Fact]
    public void SalesModel_MapsCustomerCreditProfileAndXmin()
    {
        using var context = CreateNpgsqlModelContext();
        var customer = context.Model.FindEntityType(typeof(Customer));

        Assert.NotNull(customer);
        Assert.Equal("Customers", customer!.GetTableName());
        Assert.Equal("boolean", customer.FindProperty(nameof(Customer.IsCreditEnabled))!.GetColumnType());
        Assert.Equal("integer", customer.FindProperty(nameof(Customer.PaymentTermDays))!.GetColumnType());
        Assert.Equal("integer", customer.FindProperty(nameof(Customer.CreditGraceDays))!.GetColumnType());
        Assert.Equal("integer", customer.FindProperty(nameof(Customer.CreditStatus))!.GetColumnType());

        var notes = customer.FindProperty(nameof(Customer.CreditNotes));
        Assert.NotNull(notes);
        Assert.Equal(1000, notes!.GetMaxLength());
        Assert.True(notes.IsNullable);

        var xmin = customer.FindProperty("xmin");
        Assert.NotNull(xmin);
        Assert.Equal("xid", xmin!.GetColumnType());
        Assert.True(xmin.IsConcurrencyToken);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, xmin.ValueGenerated);
    }

    [Fact]
    public void SalesModel_MapsReceivableContract()
    {
        using var context = CreateNpgsqlModelContext();
        var receivable = context.Model.FindEntityType(typeof(Receivable));

        Assert.NotNull(receivable);
        Assert.Equal("Receivables", receivable!.GetTableName());

        Assert.Equal(typeof(decimal), receivable.FindProperty(nameof(Receivable.OriginalUSD))!.ClrType);
        Assert.Equal("numeric(18,4)", receivable.FindProperty(nameof(Receivable.OriginalUSD))!.GetColumnType());
        Assert.Equal("numeric(18,4)", receivable.FindProperty(nameof(Receivable.BalanceUSD))!.GetColumnType());
        Assert.Equal("numeric(18,2)", receivable.FindProperty(nameof(Receivable.OriginalBsS))!.GetColumnType());
        Assert.Equal("numeric(18,4)", receivable.FindProperty(nameof(Receivable.HistoricalRate))!.GetColumnType());
        Assert.Equal("timestamp with time zone", receivable.FindProperty(nameof(Receivable.IssuedAtUtc))!.GetColumnType());
        Assert.Equal("timestamp with time zone", receivable.FindProperty(nameof(Receivable.DueDateUtc))!.GetColumnType());
        Assert.Equal("integer", receivable.FindProperty(nameof(Receivable.Status))!.GetColumnType());

        var saleIndex = Assert.Single(
            receivable.GetIndexes(),
            index => index.Properties.Count == 1 && index.Properties[0].Name == nameof(Receivable.SaleId));
        Assert.True(saleIndex.IsUnique);
        Assert.Equal("IX_Receivables_SaleId", saleIndex.GetDatabaseName());

        Assert.Contains(receivable.GetIndexes(), index => index.GetDatabaseName() == "IX_Receivables_CustomerId_Status");
        Assert.Contains(receivable.GetIndexes(), index => index.GetDatabaseName() == "IX_Receivables_DueDateUtc_Status");

        var saleForeignKey = Assert.Single(
            receivable.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(Sale));
        Assert.Equal(DeleteBehavior.Restrict, saleForeignKey.DeleteBehavior);
        Assert.True(saleForeignKey.IsUnique);

        var customerForeignKey = Assert.Single(
            receivable.GetForeignKeys(),
            foreignKey => foreignKey.PrincipalEntityType.ClrType == typeof(Customer));
        Assert.Equal(DeleteBehavior.Restrict, customerForeignKey.DeleteBehavior);

        var xmin = receivable.FindProperty("xmin");
        Assert.NotNull(xmin);
        Assert.Equal("xid", xmin!.GetColumnType());
        Assert.True(xmin.IsConcurrencyToken);
    }

    [Fact]
    public void SalesModel_MapsReceivablePaymentAndAllocationContracts()
    {
        using var context = CreateNpgsqlModelContext();
        var payment = context.Model.FindEntityType(typeof(ReceivablePayment));
        var allocation = context.Model.FindEntityType(typeof(CreditPaymentAllocation));

        Assert.NotNull(payment);
        Assert.NotNull(allocation);
        Assert.Equal("ReceivablePayments", payment!.GetTableName());
        Assert.Equal("CreditPaymentAllocations", allocation!.GetTableName());

        Assert.Equal("numeric(18,4)", payment.FindProperty(nameof(ReceivablePayment.AmountUSD))!.GetColumnType());
        Assert.Equal("numeric(18,2)", payment.FindProperty(nameof(ReceivablePayment.AmountBsS))!.GetColumnType());
        Assert.Equal("numeric(18,4)", payment.FindProperty(nameof(ReceivablePayment.PaymentRate))!.GetColumnType());
        Assert.Equal("numeric(18,2)", payment.FindProperty(nameof(ReceivablePayment.FxDifferenceBsS))!.GetColumnType());
        Assert.Equal("timestamp with time zone", payment.FindProperty(nameof(ReceivablePayment.ReceivedAtUtc))!.GetColumnType());

        var idempotencyKey = payment.FindProperty(nameof(ReceivablePayment.IdempotencyKey));
        Assert.NotNull(idempotencyKey);
        Assert.Equal(100, idempotencyKey!.GetMaxLength());
        Assert.True(idempotencyKey.IsNullable);

        var idempotencyIndex = Assert.Single(
            payment.GetIndexes(),
            index => index.GetDatabaseName() == "IX_ReceivablePayments_IdempotencyKey");
        Assert.True(idempotencyIndex.IsUnique);
        Assert.Equal("\"IdempotencyKey\" IS NOT NULL", idempotencyIndex.GetFilter());

        Assert.Contains(
            payment.GetIndexes(),
            index => index.GetDatabaseName() == "IX_ReceivablePayments_CustomerId_ReceivedAtUtc");
        Assert.Contains(
            payment.GetIndexes(),
            index => index.GetDatabaseName() == "IX_ReceivablePayments_CashTransactionId");
        Assert.Contains(
            payment.GetIndexes(),
            index => index.GetDatabaseName() == "IX_ReceivablePayments_PaymentMethodId");

        Assert.All(payment.GetForeignKeys(), foreignKey => Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior));

        Assert.Equal("numeric(18,4)", allocation.FindProperty(nameof(CreditPaymentAllocation.AmountUSD))!.GetColumnType());
        Assert.Equal("numeric(18,2)", allocation.FindProperty(nameof(CreditPaymentAllocation.AmountBsS))!.GetColumnType());
        Assert.Equal("numeric(18,2)", allocation.FindProperty(nameof(CreditPaymentAllocation.FxDifferenceBsS))!.GetColumnType());

        var allocationUnique = Assert.Single(
            allocation.GetIndexes(),
            index => index.GetDatabaseName() == "IX_CreditPaymentAllocations_ReceivablePaymentId_ReceivableId");
        Assert.True(allocationUnique.IsUnique);
        Assert.Equal(2, allocationUnique.Properties.Count);

        Assert.Contains(
            allocation.GetIndexes(),
            index => index.GetDatabaseName() == "IX_CreditPaymentAllocations_ReceivableId");
        Assert.All(allocation.GetForeignKeys(), foreignKey => Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior));
    }

    [Fact]
    public async Task CreditGraph_RoundTrips_OnSqlite()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            var (customer, _, _, receivableA, receivableB, payment) = await SeedCreditGraphAsync(context);

            context.ChangeTracker.Clear();

            var persistedCustomer = await context.Customers
                .AsNoTracking()
                .SingleAsync(candidate => candidate.Id == customer.Id);

            Assert.True(persistedCustomer.IsCreditEnabled);
            Assert.Equal(1500.50m, persistedCustomer.CreditLimitUSD);
            Assert.Equal(15, persistedCustomer.PaymentTermDays);
            Assert.Equal(3, persistedCustomer.CreditGraceDays);
            Assert.Equal(CreditStatus.Active, persistedCustomer.CreditStatus);
            Assert.Equal("Acuerdo comercial: abonos quincenales.", persistedCustomer.CreditNotes);

            var persistedPayment = await context.ReceivablePayments
                .AsNoTracking()
                .Include(candidate => candidate.Allocations)
                .SingleAsync(candidate => candidate.Id == payment.Id);

            Assert.Equal(customer.Id, persistedPayment.CustomerId);
            Assert.Equal(100.0000m, persistedPayment.AmountUSD);
            Assert.Equal(3654.32m, persistedPayment.AmountBsS);
            Assert.Equal(36.5432m, persistedPayment.PaymentRate);
            Assert.Equal(-12.34m, persistedPayment.FxDifferenceBsS);
            Assert.Equal("ABONO-0001", persistedPayment.ReferenceNumber);
            Assert.Equal("credit-payment-001", persistedPayment.IdempotencyKey);
            Assert.Equal(FixedUtc.AddDays(10), persistedPayment.ReceivedAtUtc);
            Assert.Equal(2, persistedPayment.Allocations.Count);

            var allocationA = Assert.Single(
                persistedPayment.Allocations,
                candidate => candidate.ReceivableId == receivableA.Id);
            Assert.Equal(60.1234m, allocationA.AmountUSD);
            Assert.Equal(2197.17m, allocationA.AmountBsS);
            Assert.Equal(7.89m, allocationA.FxDifferenceBsS);

            var allocationB = Assert.Single(
                persistedPayment.Allocations,
                candidate => candidate.ReceivableId == receivableB.Id);
            Assert.Equal(39.8766m, allocationB.AmountUSD);
            Assert.Equal(1457.15m, allocationB.AmountBsS);
            Assert.Equal(-20.23m, allocationB.FxDifferenceBsS);

            var persistedReceivables = await context.Receivables
                .AsNoTracking()
                .OrderBy(candidate => candidate.Id)
                .ToListAsync();

            Assert.Equal(2, persistedReceivables.Count);
            Assert.Equal(receivableA.Id, persistedReceivables[0].Id);
            Assert.Equal(150.1234m, persistedReceivables[0].OriginalUSD);
            Assert.Equal(90.1234m, persistedReceivables[0].BalanceUSD);
            Assert.Equal(4578.76m, persistedReceivables[0].OriginalBsS);
            Assert.Equal(30.5000m, persistedReceivables[0].HistoricalRate);
            Assert.Equal(FixedUtc, persistedReceivables[0].IssuedAtUtc);
            Assert.Equal(FixedUtc.AddDays(15), persistedReceivables[0].DueDateUtc);
            Assert.Equal(ReceivableStatus.Parcial, persistedReceivables[0].Status);
            Assert.Equal(receivableB.Id, persistedReceivables[1].Id);
            Assert.Equal(75.4321m, persistedReceivables[1].BalanceUSD);
            Assert.Equal(ReceivableStatus.Vigente, persistedReceivables[1].Status);
        }
    }

    [Fact]
    public async Task DeletingSaleReferencedByReceivable_IsRestricted_OnSqlite()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            var (_, saleA, _, _, _, _) = await SeedCreditGraphAsync(context);
            context.ChangeTracker.Clear();

            var trackedSale = await context.Sales.SingleAsync(candidate => candidate.Id == saleA.Id);
            context.Sales.Remove(trackedSale);

            var rejected = await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            Assert.NotNull(rejected.InnerException);
        }
    }

    [Fact]
    public async Task CreditUniquenessGuards_RejectDuplicates_OnSqlite()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            var (customer, saleA, _, receivableA, _, payment) = await SeedCreditGraphAsync(context);
            context.ChangeTracker.Clear();

            // Un segundo Receivable sobre la misma venta viola el 1:1 (IX_Receivables_SaleId).
            context.Receivables.Add(new Receivable
            {
                SaleId = saleA.Id,
                CustomerId = customer.Id,
                OriginalUSD = 1m,
                BalanceUSD = 1m,
                OriginalBsS = 30.50m,
                HistoricalRate = 30.5000m,
                IssuedAtUtc = FixedUtc,
                DueDateUtc = FixedUtc.AddDays(15)
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            context.ChangeTracker.Clear();

            // IdempotencyKey repetida viola el indice unico filtrado del cobro.
            context.ReceivablePayments.Add(new ReceivablePayment
            {
                CustomerId = customer.Id,
                AmountUSD = 1m,
                AmountBsS = 36.54m,
                PaymentRate = 36.5432m,
                ReceivedAtUtc = FixedUtc,
                IdempotencyKey = "credit-payment-001"
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            context.ChangeTracker.Clear();

            // Imputacion duplicada del mismo abono contra la misma obligacion.
            context.CreditPaymentAllocations.Add(new CreditPaymentAllocation
            {
                ReceivablePaymentId = payment.Id,
                ReceivableId = receivableA.Id,
                AmountUSD = 1m,
                AmountBsS = 36.54m
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            context.ChangeTracker.Clear();
        }
    }

    [Fact]
    public void Migration_AddCreditReceivableCxcRev8187_IsAdditiveAndGuarded()
    {
        using var context = CreateNpgsqlModelContext();
        var migrations = context.GetService<IMigrationsAssembly>();

        Assert.Contains("20261017120000_AddCreditReceivableCxcRev8187", migrations.Migrations.Keys);

        var migration = (Migration)Activator.CreateInstance(
            migrations.Migrations["20261017120000_AddCreditReceivableCxcRev8187"].AsType())!;
        Assert.Empty(migration.UpOperations.OfType<DropTableOperation>());
        Assert.Empty(migration.UpOperations.OfType<DropColumnOperation>());

        var script = context.GetService<IMigrator>().GenerateScript(
            fromMigration: "20261016020937_AddSaleItemRoundingRuleRev8185",
            toMigration: "20261017120000_AddCreditReceivableCxcRev8187");

        Assert.Contains("CREATE TABLE IF NOT EXISTS \"Receivables\"", script);
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"ReceivablePayments\"", script);
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"CreditPaymentAllocations\"", script);
        Assert.Contains("information_schema.columns", script);
        Assert.Contains("\"Customers\" ADD COLUMN \"IsCreditEnabled\"", script);
        Assert.Contains("IX_Receivables_SaleId", script);
        Assert.Contains("IX_Receivables_CustomerId_Status", script);
        Assert.Contains("IX_Receivables_DueDateUtc_Status", script);
        Assert.Contains("IX_ReceivablePayments_IdempotencyKey", script);
        Assert.Contains("WHERE \"IdempotencyKey\" IS NOT NULL", script);
        Assert.Contains("IX_CreditPaymentAllocations_ReceivablePaymentId_ReceivableId", script);
        Assert.Contains("CREATE UNIQUE INDEX", script);
        Assert.DoesNotContain("DROP TABLE", script);
    }

    [Fact]
    public void SalesModel_MapsCreditReturnContracts()
    {
        using var context = CreateNpgsqlModelContext();
        var creditReturn = context.Model.FindEntityType(typeof(CreditReturn));
        var creditReturnItem = context.Model.FindEntityType(typeof(CreditReturnItem));

        Assert.NotNull(creditReturn);
        Assert.NotNull(creditReturnItem);
        Assert.Equal("CreditReturns", creditReturn!.GetTableName());
        Assert.Equal("CreditReturnItems", creditReturnItem!.GetTableName());

        Assert.Equal("numeric(18,4)", creditReturn.FindProperty(nameof(CreditReturn.ReturnedUSD))!.GetColumnType());
        Assert.Equal("numeric(18,4)", creditReturn.FindProperty(nameof(CreditReturn.RefundedUSD))!.GetColumnType());
        Assert.Equal("numeric(18,2)", creditReturn.FindProperty(nameof(CreditReturn.RefundedBsS))!.GetColumnType());
        Assert.Equal("timestamp with time zone", creditReturn.FindProperty(nameof(CreditReturn.CreatedAtUtc))!.GetColumnType());

        var refundRate = creditReturn.FindProperty(nameof(CreditReturn.RefundRate));
        Assert.NotNull(refundRate);
        Assert.Equal("numeric(18,4)", refundRate!.GetColumnType());
        Assert.True(refundRate.IsNullable);

        var reason = creditReturn.FindProperty(nameof(CreditReturn.Reason));
        Assert.NotNull(reason);
        Assert.Equal(500, reason!.GetMaxLength());
        Assert.True(reason.IsNullable);

        var idempotencyKey = creditReturn.FindProperty(nameof(CreditReturn.IdempotencyKey));
        Assert.NotNull(idempotencyKey);
        Assert.Equal(100, idempotencyKey!.GetMaxLength());
        Assert.True(idempotencyKey.IsNullable);

        var idempotencyIndex = Assert.Single(
            creditReturn.GetIndexes(),
            index => index.GetDatabaseName() == "IX_CreditReturns_IdempotencyKey");
        Assert.True(idempotencyIndex.IsUnique);
        Assert.Equal("\"IdempotencyKey\" IS NOT NULL", idempotencyIndex.GetFilter());

        // FKs Restrict: venta, obligación, cliente, transacción de caja del reembolso y usuario creador.
        Assert.Equal(5, creditReturn.GetForeignKeys().Count());
        Assert.All(creditReturn.GetForeignKeys(), foreignKey => Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior));

        Assert.Equal("numeric(18,3)", creditReturnItem.FindProperty(nameof(CreditReturnItem.Quantity))!.GetColumnType());
        Assert.Equal("numeric(18,4)", creditReturnItem.FindProperty(nameof(CreditReturnItem.AmountUSD))!.GetColumnType());
        Assert.Contains(
            creditReturnItem.GetIndexes(),
            index => index.GetDatabaseName() == "IX_CreditReturnItems_CreditReturnId");
        Assert.Contains(
            creditReturnItem.GetIndexes(),
            index => index.GetDatabaseName() == "IX_CreditReturnItems_SaleItemId");
        Assert.All(creditReturnItem.GetForeignKeys(), foreignKey => Assert.Equal(DeleteBehavior.Restrict, foreignKey.DeleteBehavior));
    }

    [Fact]
    public async Task CreditReturnGraph_RoundTrips_OnSqlite()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteSalesDbContext();
        using (connection)
        using (context)
        {
            var customer = new Customer
            {
                CedulaOrRif = "V-11223344",
                Name = "Cliente Devolución",
                IsCreditEnabled = true,
                CreditLimitUSD = 500m
            };

            var sale = new Sale
            {
                Customer = customer,
                InvoiceNumber = 8801,
                Date = FixedUtc,
                Status = SaleStatus.Completed,
                AppliedRate = 30.5000m,
                TotalUSD = 120m,
                TotalBsS = 3660m
            };
            sale.Items.Add(new SaleItem
            {
                ProductId = 11,
                ProductName = "Cemento",
                Quantity = 2m,
                UnitPrice = 60m,
                Subtotal = 120m,
                UnitPriceBsS = 1830m,
                SubtotalBsS = 3660m
            });

            var receivable = new Receivable
            {
                Sale = sale,
                Customer = customer,
                OriginalUSD = 120m,
                BalanceUSD = 90m,
                OriginalBsS = 3660m,
                HistoricalRate = 30.5000m,
                IssuedAtUtc = FixedUtc,
                DueDateUtc = FixedUtc.AddDays(30),
                Status = ReceivableStatus.Parcial
            };

            var creditReturn = new CreditReturn
            {
                Sale = sale,
                Receivable = receivable,
                Customer = customer,
                ReturnedUSD = 30.1234m,
                RefundedUSD = 5.4321m,
                RefundedBsS = 165.68m,
                RefundRate = 30.5000m,
                CreatedAtUtc = FixedUtc.AddDays(2),
                Reason = "Devolución parcial aprobada",
                IdempotencyKey = "credit-return-001"
            };
            creditReturn.Items.Add(new CreditReturnItem
            {
                SaleItem = sale.Items[0],
                Quantity = 0.5m,
                AmountUSD = 30.1234m
            });

            context.AddRange(customer, sale, receivable, creditReturn);
            await context.SaveChangesAsync();
            context.ChangeTracker.Clear();

            var persisted = await context.CreditReturns
                .AsNoTracking()
                .Include(candidate => candidate.Items)
                .SingleAsync();

            Assert.Equal(sale.Id, persisted.SaleId);
            Assert.Equal(receivable.Id, persisted.ReceivableId);
            Assert.Equal(customer.Id, persisted.CustomerId);
            Assert.Equal(30.1234m, persisted.ReturnedUSD);
            Assert.Equal(5.4321m, persisted.RefundedUSD);
            Assert.Equal(165.68m, persisted.RefundedBsS);
            Assert.Equal(30.5000m, persisted.RefundRate);
            Assert.Equal(FixedUtc.AddDays(2), persisted.CreatedAtUtc);
            Assert.Equal("Devolución parcial aprobada", persisted.Reason);
            Assert.Null(persisted.CashTransactionId);
            Assert.Null(persisted.CreatedByUserId);
            Assert.Equal("credit-return-001", persisted.IdempotencyKey);

            var persistedLine = Assert.Single(persisted.Items);
            Assert.Equal(sale.Items[0].Id, persistedLine.SaleItemId);
            Assert.Equal(0.5m, persistedLine.Quantity);
            Assert.Equal(30.1234m, persistedLine.AmountUSD);

            // Clave de idempotencia repetida: viola el índice único filtrado de la devolución.
            context.CreditReturns.Add(new CreditReturn
            {
                SaleId = sale.Id,
                ReceivableId = receivable.Id,
                CustomerId = customer.Id,
                ReturnedUSD = 1m,
                CreatedAtUtc = FixedUtc,
                IdempotencyKey = "credit-return-001"
            });
            await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
            context.ChangeTracker.Clear();
        }
    }

    [Fact]
    public void Migration_AddCreditReturn_IsAdditiveAndGuarded()
    {
        using var context = CreateNpgsqlModelContext();
        var migrations = context.GetService<IMigrationsAssembly>();

        var migrationKey = Assert.Single(migrations.Migrations.Keys, key => key.Contains("AddCreditReturn"));
        var migration = (Migration)Activator.CreateInstance(migrations.Migrations[migrationKey].AsType())!;
        Assert.Empty(migration.UpOperations.OfType<DropTableOperation>());
        Assert.Empty(migration.UpOperations.OfType<DropColumnOperation>());

        var script = context.GetService<IMigrator>().GenerateScript(
            fromMigration: "20261017120000_AddCreditReceivableCxcRev8187",
            toMigration: migrationKey);

        Assert.Contains("CREATE TABLE IF NOT EXISTS \"CreditReturns\"", script);
        Assert.Contains("CREATE TABLE IF NOT EXISTS \"CreditReturnItems\"", script);
        Assert.Contains("IX_CreditReturns_IdempotencyKey", script);
        Assert.Contains("WHERE \"IdempotencyKey\" IS NOT NULL", script);
        Assert.Contains("IX_CreditReturnItems_CreditReturnId", script);
        Assert.Contains("IX_CreditReturnItems_SaleItemId", script);
        Assert.Contains("CREATE UNIQUE INDEX", script);
        Assert.DoesNotContain("DROP TABLE", script);
    }

    private static SalesDbContext CreateNpgsqlModelContext()
    {
        var options = new DbContextOptionsBuilder<SalesDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused")
            .Options;
        return new SalesDbContext(options);
    }

    private static async Task<(Customer Customer, Sale SaleA, Sale SaleB, Receivable ReceivableA, Receivable ReceivableB, ReceivablePayment Payment)>
        SeedCreditGraphAsync(SalesDbContext context)
    {
        var customer = new Customer
        {
            CedulaOrRif = "V-99887766",
            Name = "Cliente Credito",
            Phone = "0414-0000000",
            IsCreditEnabled = true,
            CreditLimitUSD = 1500.50m,
            PaymentTermDays = 15,
            CreditGraceDays = 3,
            CreditStatus = CreditStatus.Active,
            CreditNotes = "Acuerdo comercial: abonos quincenales."
        };

        var saleA = new Sale
        {
            Customer = customer,
            InvoiceNumber = 9001,
            Date = FixedUtc,
            Status = SaleStatus.Completed,
            AppliedRate = 30.5000m,
            TotalUSD = 150.12m,
            TotalBsS = 4578.76m
        };

        var saleB = new Sale
        {
            Customer = customer,
            InvoiceNumber = 9002,
            Date = FixedUtc,
            Status = SaleStatus.Completed,
            AppliedRate = 30.5000m,
            TotalUSD = 75.43m,
            TotalBsS = 2300.00m
        };

        var receivableA = new Receivable
        {
            Sale = saleA,
            Customer = customer,
            OriginalUSD = 150.1234m,
            BalanceUSD = 90.1234m,
            OriginalBsS = 4578.76m,
            HistoricalRate = 30.5000m,
            IssuedAtUtc = FixedUtc,
            DueDateUtc = FixedUtc.AddDays(15),
            Status = ReceivableStatus.Parcial
        };

        var receivableB = new Receivable
        {
            Sale = saleB,
            Customer = customer,
            OriginalUSD = 75.4321m,
            BalanceUSD = 75.4321m,
            OriginalBsS = 2300.00m,
            HistoricalRate = 30.5000m,
            IssuedAtUtc = FixedUtc,
            DueDateUtc = FixedUtc.AddDays(15),
            Status = ReceivableStatus.Vigente
        };

        var payment = new ReceivablePayment
        {
            Customer = customer,
            AmountUSD = 100.0000m,
            AmountBsS = 3654.32m,
            PaymentRate = 36.5432m,
            FxDifferenceBsS = -12.34m,
            ReferenceNumber = "ABONO-0001",
            IdempotencyKey = "credit-payment-001",
            ReceivedAtUtc = FixedUtc.AddDays(10)
        };

        payment.Allocations.Add(new CreditPaymentAllocation
        {
            Receivable = receivableA,
            AmountUSD = 60.1234m,
            AmountBsS = 2197.17m,
            FxDifferenceBsS = 7.89m
        });

        payment.Allocations.Add(new CreditPaymentAllocation
        {
            Receivable = receivableB,
            AmountUSD = 39.8766m,
            AmountBsS = 1457.15m,
            FxDifferenceBsS = -20.23m
        });

        context.AddRange(customer, saleA, saleB, receivableA, receivableB, payment);
        await context.SaveChangesAsync();

        return (customer, saleA, saleB, receivableA, receivableB, payment);
    }
}
