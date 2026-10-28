using Microsoft.EntityFrameworkCore;
using Sales.Module.Entities;
using Core.Entities;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Backend.API")]
namespace Sales.Module.Data;

public class SalesDbContext : DbContext
{
    public SalesDbContext(DbContextOptions<SalesDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users { get; set; } = null!;
    public DbSet<Customer> Customers { get; set; } = null!;
    public DbSet<Sale> Sales { get; set; } = null!;
    public DbSet<SaleItem> SaleItems { get; set; } = null!;
    public DbSet<SaleDelivery> SaleDeliveries { get; set; } = null!;
    public DbSet<SaleDeliveryItem> SaleDeliveryItems { get; set; } = null!;
    public DbSet<PaymentMethod> PaymentMethods { get; set; } = null!;
    public DbSet<SalePayment> SalePayments { get; set; } = null!;
    public DbSet<CashDrawerSession> CashDrawerSessions { get; set; } = null!;
    public DbSet<CashTransaction> CashTransactions { get; set; } = null!;
    public DbSet<DailyClosure> DailyClosures { get; set; } = null!;
    public DbSet<ClosureDetail> ClosureDetails { get; set; } = null!;
    public DbSet<OutboxMessage> OutboxMessages { get; set; } = null!;
    public DbSet<IdempotentRequest> IdempotentRequests { get; set; } = null!;
    public DbSet<AuthorizationRequest> AuthorizationRequests { get; set; } = null!;
    public DbSet<AuthorizationAudit> AuthorizationAudits { get; set; } = null!;
    public DbSet<Receivable> Receivables { get; set; } = null!;
    public DbSet<ReceivablePayment> ReceivablePayments { get; set; } = null!;
    public DbSet<CreditPaymentAllocation> CreditPaymentAllocations { get; set; } = null!;
    public DbSet<CreditReturn> CreditReturns { get; set; } = null!;
    public DbSet<CreditReturnItem> CreditReturnItems { get; set; } = null!;
    public DbSet<PettyCashExpense> PettyCashExpenses { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Customer configuration
        modelBuilder.Entity<Customer>()
            .HasIndex(c => c.CedulaOrRif)
            .IsUnique()
            .HasDatabaseName("IX_Customers_CedulaOrRif");

        modelBuilder.Entity<Customer>()
            .HasIndex(c => c.Name)
            .HasDatabaseName("IX_Customers_Name");

        modelBuilder.Entity<Customer>()
            .HasIndex(c => new { c.IsActive, c.Name })
            .HasDatabaseName("IX_Customers_Active_Name")
            .HasFilter("\"IsActive\" = true");

        modelBuilder.Entity<Customer>()
            .Property(c => c.CreditLimitUSD)
            .HasColumnType("decimal(18,2)");

        // User configurations
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Cedula)
            .IsUnique();

        // 8.142: la unicidad de Username la garantiza el indice funcional case-insensitive
        // ix_users_username_lower (migracion 20260823120000, SQL crudo: EF no modela indices
        // funcionales). La declaracion previa HasIndex(Username).IsUnique() era drift puro: el
        // modelo la declaraba, ninguna migracion la creaba y la base nunca tuvo ese indice
        // (los tests si, por EnsureCreated). Detectado por MigrationIndexDriftTests.

modelBuilder.Entity<User>().HasData(
            new User
            {
                Id = 1,
                Cedula = "12345678",
                Name = "Administrador",
                Username = "admin",
                Role = UserRole.Admin,
                IsActive = true,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc),
                SecurityStamp = ""
            }
        );

        modelBuilder.Entity<Sale>()
            .HasOne(s => s.Customer)
            .WithMany()
            .HasForeignKey(s => s.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Sale>()
            .HasOne(s => s.Cashier)
            .WithMany()
            .HasForeignKey(s => s.CashierId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<Sale>()
            .HasMany(s => s.Items)
            .WithOne(i => i.Sale)
            .HasForeignKey(i => i.SaleId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<Sale>()
            .HasMany(s => s.Payments)
            .WithOne(p => p.Sale)
            .HasForeignKey(p => p.SaleId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SaleDelivery>(entity =>
        {
            entity.HasOne<Sale>()
                .WithMany()
                .HasForeignKey(delivery => delivery.SaleId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(delivery => delivery.DeliveredByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.Property(delivery => delivery.DeliveredByName)
                .HasColumnType("text")
                .IsRequired();

            entity.HasIndex(delivery => delivery.SaleId)
                .HasDatabaseName("IX_SaleDeliveries_SaleId");
        });

        modelBuilder.Entity<SaleDeliveryItem>(entity =>
        {
            entity.HasOne<SaleDelivery>()
                .WithMany(delivery => delivery.Items)
                .HasForeignKey(deliveryItem => deliveryItem.SaleDeliveryId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne<SaleItem>()
                .WithMany()
                .HasForeignKey(deliveryItem => deliveryItem.SaleItemId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.Property(deliveryItem => deliveryItem.ProductName)
                .IsRequired();

            entity.Property(deliveryItem => deliveryItem.QuantityDelivered)
                .HasColumnType("numeric(18,3)")
                .HasPrecision(18, 3);

            entity.HasIndex(deliveryItem => deliveryItem.SaleDeliveryId)
                .HasDatabaseName("IX_SaleDeliveryItems_SaleDeliveryId");

            entity.HasIndex(deliveryItem => deliveryItem.SaleItemId)
                .HasDatabaseName("IX_SaleDeliveryItems_SaleItemId");
        });

        modelBuilder.Entity<SalePayment>()
            .Property(p => p.Amount)
            .HasColumnType("decimal(18,2)");

        modelBuilder.Entity<Sale>()
            .Property(s => s.TotalUSD)
            .HasColumnType("decimal(18,2)");

        modelBuilder.Entity<Sale>()
            .Property(s => s.Subtotal)
            .HasColumnType("decimal(18,4)");

        modelBuilder.Entity<Sale>()
            .Property(s => s.AppliedRate)
            .HasColumnType("decimal(18,4)");

        modelBuilder.Entity<Sale>().Property(s => s.TotalBsS).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<Sale>().Property(s => s.SubtotalBsS).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<Sale>().Property(s => s.RoundingAdjustment).HasColumnType("decimal(18,2)");

        // 8.211-T3a (S6): snapshot monetario aditivo. Los defaults de columna backfillean las
        // filas legacy como USD/VES/Dual sin recalcular montos ni tasas. HasSentinel permite
        // insertar NULL explícito en la secundaria (modo Single) pese al default de columna:
        // sin él EF omitiría null en el INSERT y PostgreSQL escribiría "VES".
        modelBuilder.Entity<Sale>()
            .Property(s => s.PrimaryCurrencyCode)
            .IsRequired()
            .HasMaxLength(3)
            .HasDefaultValue("USD");

        modelBuilder.Entity<Sale>()
            .Property(s => s.SecondaryCurrencyCode)
            .HasMaxLength(3)
            .HasDefaultValue("VES")
            .HasSentinel(string.Empty);

        modelBuilder.Entity<Sale>()
            .Property(s => s.MonetaryMode)
            .IsRequired()
            .HasMaxLength(16)
            .HasDefaultValue("Dual");

        // 8.211-T3c2 (S6): snapshots aditivos de importes. Sin default ni índice: la migración
        // backfillea las filas legacy desde TotalUSD/TotalBsS sin recalcular montos ni tasas.
        modelBuilder.Entity<Sale>()
            .Property(s => s.PrimaryAmount)
            .HasColumnType("decimal(18,4)");

        modelBuilder.Entity<Sale>()
            .Property(s => s.SecondaryAmount)
            .HasColumnType("decimal(18,4)");

        modelBuilder.Entity<Sale>()
            .Property(s => s.AppliedRateSource)
            .HasMaxLength(16);

        modelBuilder.Entity<SaleItem>().Property(i => i.Quantity).HasColumnType("numeric(18,3)");
        modelBuilder.Entity<SaleItem>().Property(i => i.DeliveredQuantity).HasColumnType("numeric(18,3)").HasPrecision(18, 3);
        modelBuilder.Entity<SaleItem>().Property(i => i.UnitPrice).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<SaleItem>().Property(i => i.Subtotal).HasColumnType("decimal(18,4)");

        modelBuilder.Entity<Sale>()
            .HasIndex(s => s.Date);

        modelBuilder.Entity<Sale>()
            .HasIndex(s => new { s.Status, s.Date });

        modelBuilder.Entity<Sale>()
            .HasIndex(s => new { s.Status, s.DeliveryStatus });

        modelBuilder.Entity<Sale>()
            .HasIndex(s => s.CustomerId);

        modelBuilder.Entity<Sale>()
            .HasIndex(s => s.CashierId);

        modelBuilder.Entity<SaleItem>()
            .HasIndex(i => i.SaleId)
            .HasDatabaseName("IX_SaleItems_SaleId");

        // 8.142: reportes de rotacion y stock muerto por producto (Paso 15 de docs/Ideas.txt).
        // Sin FK hacia Products: la entidad vive en InventoryDbContext y no hay FKs cross-context.
        modelBuilder.Entity<SaleItem>()
            .HasIndex(i => i.ProductId)
            .HasDatabaseName("IX_SaleItems_ProductId");

        modelBuilder.Entity<SalePayment>()
            .HasIndex(p => p.SaleId)
            .HasDatabaseName("IX_SalePayments_SaleId");

        modelBuilder.Entity<SalePayment>()
            .HasIndex(p => p.PaymentMethodId);

        // 8.202-H4 (S8): ventana del Cierre Z por fecha real del pago.
        modelBuilder.Entity<SalePayment>()
            .HasIndex(p => p.CreatedAt)
            .HasDatabaseName("IX_SalePayments_CreatedAt");

        modelBuilder.Entity<Sale>()
            .HasIndex(s => s.InvoiceNumber)
            .IsUnique()
            .HasFilter("\"InvoiceNumber\" IS NOT NULL");

        modelBuilder.Entity<SaleItem>().Property(i => i.UnitPriceBsS).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<SaleItem>().Property(i => i.SubtotalBsS).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<SaleItem>().Property(i => i.UnitCostUSD).HasColumnType("decimal(18,2)");
        // 8.185-T2 (S5): regla de efectivo por línea persistida por NOMBRE (estilo 8.184 Product);
        // nullable = línea previa a 8.185 sin política registrada (techo canónico 8.104).
        modelBuilder.Entity<SaleItem>().Property(i => i.RoundingRuleCashBs).HasConversion<string>().HasMaxLength(32);

        modelBuilder.Entity<SalePayment>().Property(p => p.AmountBsS).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<SalePayment>().Property(p => p.ExchangeRate).HasColumnType("decimal(18,4)");

        // 8.211-T3b1 (S6): snapshot aditivo de la moneda del abono. Sin default ni índice: la
        // migración backfillea las filas legacy desde el método persistido (nombre solo fallback).
        modelBuilder.Entity<SalePayment>()
            .Property(p => p.CurrencyCode)
            .HasMaxLength(10);

        // 8.211-T3c2 (S6): importe original en la moneda del medio de pago (aditivo, sin default).
        modelBuilder.Entity<SalePayment>()
            .Property(p => p.OriginalAmount)
            .HasColumnType("decimal(18,4)");

        // Cash Drawer Configurations
        modelBuilder.Entity<CashDrawerSession>()
            .HasMany(s => s.Transactions)
            .WithOne(t => t.Session)
            .HasForeignKey(t => t.SessionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CashDrawerSession>()
            .HasIndex(s => s.Status)
            .IsUnique()
            .HasFilter("\"Status\" = 0")
            .HasDatabaseName("IX_CashDrawerSessions_SingleOpen");

        modelBuilder.Entity<CashTransaction>()
            .HasIndex(t => t.TransactionTime)
            .HasDatabaseName("IX_CashTransactions_TransactionTime");

        modelBuilder.Entity<CashTransaction>()
            .HasIndex(t => new { t.SessionId, t.TransactionTime });

        modelBuilder.Entity<CashTransaction>()
            .HasOne(t => t.PaymentMethod)
            .WithMany()
            .HasForeignKey(t => t.PaymentMethodId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CashTransaction>()
            .HasIndex(t => t.PaymentMethodId)
            .HasDatabaseName("IX_CashTransactions_PaymentMethodId");

        // 8.188-T1 (Paso 13, S6): el movimiento (Expense original o reversa Income) apunta al
        // gasto de caja chica con FK Restrict dedicada; el indice sirve al listado del turno.
        modelBuilder.Entity<CashTransaction>()
            .HasOne(t => t.PettyCashExpense)
            .WithMany(expense => expense.CashTransactions)
            .HasForeignKey(t => t.PettyCashExpenseId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CashTransaction>()
            .HasIndex(t => t.PettyCashExpenseId)
            .HasDatabaseName("IX_CashTransactions_PettyCashExpenseId");

        modelBuilder.Entity<CashDrawerSession>().Property(s => s.OpeningBalanceLocal).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<CashDrawerSession>().Property(s => s.OpeningExchangeRate).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<CashDrawerSession>().Property(s => s.ClosingBalanceLocal).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<CashDrawerSession>().Property(s => s.ClosingExchangeRate).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<CashDrawerSession>().Property(s => s.OpeningBalanceUsd).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<CashDrawerSession>().Property(s => s.ClosingBalanceUsd).HasColumnType("decimal(18,4)");

        // H4 (cierre 8.188): el libro de caja conserva el cuarto decimal del USD nominal (la
        // columna era numeric(18,2) y el alta de caja chica escribe 4dp: se perdia el 4to decimal
        // en Postgres). La anotacion [Column] de la entidad queda obsoleta: fluida prevalece.
        modelBuilder.Entity<CashTransaction>().Property(t => t.AmountUsd).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<CashTransaction>().Property(t => t.ExchangeRate).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<CashTransaction>().Property(t => t.AmountLocal).HasColumnType("decimal(18,2)");

        // 8.211-T3b3 (S6): snapshot de la moneda fisica del movimiento (aditivo, sin indice).
        modelBuilder.Entity<CashTransaction>().Property(t => t.CurrencyCode).HasMaxLength(10);

        // Daily Closure Configurations
        modelBuilder.Entity<DailyClosure>()
            .HasMany(dc => dc.Details)
            .WithOne(d => d.DailyClosure)
            .HasForeignKey(d => d.DailyClosureId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DailyClosure>().Property(dc => dc.TotalExpectedBsS).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<DailyClosure>().Property(dc => dc.TotalActualBsS).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<DailyClosure>().Property(dc => dc.TotalDifferenceBsS).HasColumnType("decimal(18,2)");

        // Residual 8.187-8.188: arqueo USD del cierre diario (nominal 4dp, nullable).
        modelBuilder.Entity<DailyClosure>().Property(dc => dc.ExpectedClosingBalanceUsd).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<DailyClosure>().Property(dc => dc.ActualClosingBalanceUsd).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<DailyClosure>().Property(dc => dc.DifferenceUsd).HasColumnType("decimal(18,4)");
        // 8.149 (SRE-02): un único cierre por fecha (guard transaccional + índice único).
        modelBuilder.Entity<DailyClosure>().HasIndex(dc => dc.ClosureDate).IsUnique();

        // Quick win 8.188 (Paso 13): el cierre ancla su renglon OPEX a la sesion de caja que
        // cierra (FK Restrict: una sesion con cierre no se borra). Cierres legados: SessionId null.
        modelBuilder.Entity<DailyClosure>()
            .HasOne(dc => dc.Session)
            .WithMany()
            .HasForeignKey(dc => dc.SessionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<DailyClosure>()
            .HasIndex(dc => dc.SessionId)
            .HasDatabaseName("IX_DailyClosures_SessionId");

        modelBuilder.Entity<ClosureDetail>().Property(cd => cd.ExpectedAmountBsS).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<ClosureDetail>().Property(cd => cd.ActualAmountBsS).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<ClosureDetail>().Property(cd => cd.DifferenceBsS).HasColumnType("decimal(18,2)");

        // PaymentMethods Configuration
        modelBuilder.Entity<PaymentMethod>()
            .Property(p => p.IsDeleted)
            .HasDefaultValue(false);

        // 8.201: moneda persistida de la gaveta. Nullable para filas legadas; la migración
        // backfillea por la regla histórica del nombre y luego los renombres no la alteran.
        modelBuilder.Entity<PaymentMethod>()
            .Property(p => p.Currency)
            .HasMaxLength(10);

        if (Database.ProviderName != "Microsoft.EntityFrameworkCore.Sqlite")
        {
            modelBuilder.Entity<PaymentMethod>()
                .Property<uint>("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        }

        // Tokens de concurrencia basados en `xmin` de PostgreSQL (hallazgo 8.2-A1).
        // `xmin` es una pseudo-columna de sistema que toda tabla PostgreSQL expone, por lo
        // que se configura en caliente sin DDL adicional y se omite en SQLite (tests).
        // Paso 11/12 (F1): Customer (perfil crediticio) y Receivable (saldo CxC) se editan
        // concurrentemente desde caja/administracion, por lo que entran al mismo regimen.
        if (Database.ProviderName != "Microsoft.EntityFrameworkCore.Sqlite")
        {
            modelBuilder.Entity<Sale>()
                .Property<uint>("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            modelBuilder.Entity<SaleItem>()
                .Property<uint>("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            modelBuilder.Entity<CashDrawerSession>()
                .Property<uint>("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            modelBuilder.Entity<Customer>()
                .Property<uint>("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();

            modelBuilder.Entity<Receivable>()
                .Property<uint>("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        }

        // Seed initial payment methods
        modelBuilder.Entity<PaymentMethod>().HasData(
            new PaymentMethod { Id = 1, Name = "Cash", IsActive = true, RequiresReference = false, IsCash = true, IsDeleted = false, Currency = PaymentMethodCurrencyResolver.LocalCurrency },
            new PaymentMethod { Id = 2, Name = "Card", IsActive = true, RequiresReference = true, IsCash = false, IsDeleted = false, Currency = PaymentMethodCurrencyResolver.LocalCurrency }
        );

        // Sequences for consecutive invoice numbers (H-SAL-1 / A2) and atomic DisplayOrder (PostgreSQL)
        if (Database.ProviderName != "Microsoft.EntityFrameworkCore.Sqlite")
        {
            modelBuilder.HasSequence<int>("factura_number_seq")
                .StartsAt(1)
                .IncrementsBy(1);

            modelBuilder.HasSequence<int>("paymentmethod_displayorder_seq")
                .StartsAt(1)
                .IncrementsBy(1);

            // 8.188-T1 (Paso 13, S6): correlativo unico del vale de caja chica
            // (presentacion VCC-000001). El default de la columna se declara solo para
            // Npgsql: en SQLite (tests) los correlativos se fijan explicitamente.
            modelBuilder.HasSequence<long>("petty_cash_voucher_seq")
                .StartsAt(1)
                .IncrementsBy(1);

            modelBuilder.Entity<PettyCashExpense>()
                .Property(expense => expense.VoucherNumber)
                .HasDefaultValueSql("nextval('petty_cash_voucher_seq')");
        }

        // OutboxMessages Configuration
        modelBuilder.Entity<OutboxMessage>(entity =>
        {
            entity.HasKey(e => e.Id);
            entity.Property(e => e.EventType).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Status).HasMaxLength(20).IsRequired();
            entity.Property(e => e.Payload).HasColumnType("jsonb").IsRequired();
            entity.HasIndex(e => new { e.Status, e.NextRetryUtc })
                .HasDatabaseName("IX_OutboxMessages_Status_NextRetryUtc")
                .HasFilter("\"Status\" = 'Pending'");
            entity.HasIndex(e => e.CreatedAtUtc)
                .HasDatabaseName("IX_OutboxMessages_CreatedAtUtc");
        });

        // IdempotentRequests Configuration
        modelBuilder.Entity<IdempotentRequest>(entity =>
        {
            entity.ToTable("IdempotentRequests");
            entity.HasKey(e => e.Id);
            
            // Índice único compuesto (Key, RequestPath)
            entity.HasIndex(e => new { e.Key, e.RequestPath })
                .IsUnique()
                .HasDatabaseName("IX_IdempotentRequests_Key_RequestPath");
                
            // Índice secundario para optimizar la purga periódica
            entity.HasIndex(e => e.ExpiresAtUtc)
                .HasDatabaseName("IX_IdempotentRequests_ExpiresAtUtc");

            // Índice secundario para auditoría y consultas
            entity.HasIndex(e => e.CreatedAtUtc)
                .HasDatabaseName("IX_IdempotentRequests_CreatedAtUtc");

            entity.Property(e => e.Key)
                .HasMaxLength(128)
                .IsRequired();

            entity.Property(e => e.RequestPath)
                .HasMaxLength(256)
                .IsRequired();

            // Hash binario SHA-256 de 32 bytes (tipo bytea en PostgreSQL)
            entity.Property(e => e.PayloadHash)
                .HasColumnType("bytea")
                .IsRequired();

            entity.Property(e => e.ResponseBody)
                .IsRequired();
        });

        // 8.150 (T1): hub de autorizaciones remotas (design D1). Sin FK ni navegaciones:
        // los nombres quedan snapshoteados y las ediciones/bajas de usuarios no reescriben
        // la auditoria ni bloquean su eliminacion.
        modelBuilder.Entity<AuthorizationRequest>(entity =>
        {
            // Dedupe atomico: una unica Pending por (cajero, venta, accion); el filtro usa
            // el valor 0 de AuthorizationStatus.Pending (convencion int de SaleStatus).
            entity.HasIndex(request => new { request.RequestedByUserId, request.SaleId, request.ActionType })
                .IsUnique()
                .HasFilter("\"Status\" = 0")
                .HasDatabaseName("IX_AuthorizationRequests_PendingDedupe");

            entity.HasIndex(request => new { request.Status, request.ExpiresAt })
                .HasDatabaseName("IX_AuthorizationRequests_Status_ExpiresAt");

            entity.Property(request => request.ContextHash)
                .HasMaxLength(64)
                .IsRequired();
        });

        modelBuilder.Entity<AuthorizationAudit>(entity =>
        {
            entity.HasIndex(audit => audit.RequestId)
                .HasDatabaseName("IX_AuthorizationAudits_RequestId");
        });

        // Paso 11 + 12 (F1): dominio CxC bimonetario. La obligacion se denomina en USD 4dp
        // con snapshot de la tasa de emision; los abonos congelan la tasa del dia. Sin cascade
        // delete en ninguna relacion: la integridad contable exige bajas explicitas y auditadas.
        modelBuilder.Entity<Receivable>(entity =>
        {
            entity.HasOne(receivable => receivable.Sale)
                .WithOne()
                .HasForeignKey<Receivable>(receivable => receivable.SaleId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(receivable => receivable.Customer)
                .WithMany()
                .HasForeignKey(receivable => receivable.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);

            // Una factura a credito genera a lo sumo una obligacion.
            entity.HasIndex(receivable => receivable.SaleId)
                .IsUnique()
                .HasDatabaseName("IX_Receivables_SaleId");

            entity.HasIndex(receivable => new { receivable.CustomerId, receivable.Status })
                .HasDatabaseName("IX_Receivables_CustomerId_Status");

            entity.HasIndex(receivable => new { receivable.DueDateUtc, receivable.Status })
                .HasDatabaseName("IX_Receivables_DueDateUtc_Status");

            entity.Property(receivable => receivable.OriginalUSD).HasColumnType("decimal(18,4)");
            entity.Property(receivable => receivable.BalanceUSD).HasColumnType("decimal(18,4)");
            entity.Property(receivable => receivable.OriginalBsS).HasColumnType("decimal(18,2)");
            entity.Property(receivable => receivable.HistoricalRate).HasColumnType("decimal(18,4)");
        });

        modelBuilder.Entity<ReceivablePayment>(entity =>
        {
            entity.HasOne(payment => payment.Customer)
                .WithMany()
                .HasForeignKey(payment => payment.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(payment => payment.CashTransaction)
                .WithMany()
                .HasForeignKey(payment => payment.CashTransactionId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(payment => payment.PaymentMethod)
                .WithMany()
                .HasForeignKey(payment => payment.PaymentMethodId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(payment => payment.ReceivedByUser)
                .WithMany()
                .HasForeignKey(payment => payment.ReceivedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(payment => payment.Allocations)
                .WithOne(allocation => allocation.ReceivablePayment)
                .HasForeignKey(allocation => allocation.ReceivablePaymentId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(payment => payment.CashTransactionId)
                .HasDatabaseName("IX_ReceivablePayments_CashTransactionId");

            entity.HasIndex(payment => new { payment.CustomerId, payment.ReceivedAtUtc })
                .HasDatabaseName("IX_ReceivablePayments_CustomerId_ReceivedAtUtc");

            // Idempotencia del cobro: unica solo cuando la clave existe (NULL multiple permitido).
            entity.HasIndex(payment => payment.IdempotencyKey)
                .IsUnique()
                .HasFilter("\"IdempotencyKey\" IS NOT NULL")
                .HasDatabaseName("IX_ReceivablePayments_IdempotencyKey");

            entity.HasIndex(payment => payment.PaymentMethodId)
                .HasDatabaseName("IX_ReceivablePayments_PaymentMethodId");

            // 8.202-H4 (S8): ventana del Cierre Z por fecha real del abono CxC.
            entity.HasIndex(payment => payment.ReceivedAtUtc)
                .HasDatabaseName("IX_ReceivablePayments_ReceivedAtUtc");

            entity.HasIndex(payment => payment.ReceivedByUserId)
                .HasDatabaseName("IX_ReceivablePayments_ReceivedByUserId");

            entity.Property(payment => payment.AmountUSD).HasColumnType("decimal(18,4)");
            entity.Property(payment => payment.AmountBsS).HasColumnType("decimal(18,2)");
            entity.Property(payment => payment.PaymentRate).HasColumnType("decimal(18,4)");
            entity.Property(payment => payment.FxDifferenceBsS).HasColumnType("decimal(18,2)");
        });

        modelBuilder.Entity<CreditPaymentAllocation>(entity =>
        {
            entity.HasOne(allocation => allocation.Receivable)
                .WithMany(receivable => receivable.PaymentAllocations)
                .HasForeignKey(allocation => allocation.ReceivableId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(allocation => allocation.ReceivableId)
                .HasDatabaseName("IX_CreditPaymentAllocations_ReceivableId");

            // Una misma obligacion no puede recibir dos imputaciones del mismo abono.
            entity.HasIndex(allocation => new { allocation.ReceivablePaymentId, allocation.ReceivableId })
                .IsUnique()
                .HasDatabaseName("IX_CreditPaymentAllocations_ReceivablePaymentId_ReceivableId");

            entity.Property(allocation => allocation.AmountUSD).HasColumnType("decimal(18,4)");
            entity.Property(allocation => allocation.AmountBsS).HasColumnType("decimal(18,2)");
            entity.Property(allocation => allocation.FxDifferenceBsS).HasColumnType("decimal(18,2)");
        });

        // T3b2 (Paso 11/12, S5/S7): devoluciones y anulaciones de ventas a credito. Sin cascade
        // delete: la traza contable exige bajas explicitas y auditadas; el reembolso del overpago
        // referencia la transaccion de caja (Expense, origen CreditRefund).
        modelBuilder.Entity<CreditReturn>(entity =>
        {
            entity.HasOne(creditReturn => creditReturn.Sale)
                .WithMany()
                .HasForeignKey(creditReturn => creditReturn.SaleId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(creditReturn => creditReturn.Receivable)
                .WithMany()
                .HasForeignKey(creditReturn => creditReturn.ReceivableId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(creditReturn => creditReturn.Customer)
                .WithMany()
                .HasForeignKey(creditReturn => creditReturn.CustomerId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(creditReturn => creditReturn.CashTransaction)
                .WithMany()
                .HasForeignKey(creditReturn => creditReturn.CashTransactionId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne(creditReturn => creditReturn.CreatedByUser)
                .WithMany()
                .HasForeignKey(creditReturn => creditReturn.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasMany(creditReturn => creditReturn.Items)
                .WithOne(item => item.CreditReturn)
                .HasForeignKey(item => item.CreditReturnId)
                .OnDelete(DeleteBehavior.Restrict);

            // Idempotencia de la devolucion: unica solo cuando la clave existe.
            entity.HasIndex(creditReturn => creditReturn.IdempotencyKey)
                .IsUnique()
                .HasFilter("\"IdempotencyKey\" IS NOT NULL")
                .HasDatabaseName("IX_CreditReturns_IdempotencyKey");

            entity.Property(creditReturn => creditReturn.ReturnedUSD).HasColumnType("decimal(18,4)");
            entity.Property(creditReturn => creditReturn.RefundedUSD).HasColumnType("decimal(18,4)");
            entity.Property(creditReturn => creditReturn.RefundedBsS).HasColumnType("decimal(18,2)");
            entity.Property(creditReturn => creditReturn.RefundRate).HasColumnType("decimal(18,4)");
        });

        modelBuilder.Entity<CreditReturnItem>(entity =>
        {
            entity.HasOne(item => item.SaleItem)
                .WithMany()
                .HasForeignKey(item => item.SaleItemId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(item => item.CreditReturnId)
                .HasDatabaseName("IX_CreditReturnItems_CreditReturnId");

            entity.HasIndex(item => item.SaleItemId)
                .HasDatabaseName("IX_CreditReturnItems_SaleItemId");

            entity.Property(item => item.Quantity).HasColumnType("numeric(18,3)");
            entity.Property(item => item.AmountUSD).HasColumnType("decimal(18,4)");
        });

        // T1 (Paso 13 / 8.188, S2/S6): gastos operativos de caja chica. El gasto pertenece a la
        // sesion de caja (FK Restrict obligatoria) y su libro transaccional vive en CashTransaction
        // (PettyCashExpenseId); sin cascade delete: la anulacion es logica y auditada.
        modelBuilder.Entity<PettyCashExpense>(entity =>
        {
            entity.HasOne(expense => expense.Session)
                .WithMany()
                .HasForeignKey(expense => expense.SessionId)
                .OnDelete(DeleteBehavior.Restrict);

            // Trazabilidad de usuarios (creador/autorizador/anulador) y de la autorizacion
            // jerarquica consumida; sin navegaciones: los nombres no se snapshotean aqui.
            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(expense => expense.CreatedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(expense => expense.AuthorizedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(expense => expense.CancelledByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            // H3 (cierre 8.188): supervisor que aprobo la anulacion (nullable; solo se completa
            // cuando hubo autorizacion jerarquica con un aprobador distinto del ejecutor).
            entity.HasOne<User>()
                .WithMany()
                .HasForeignKey(expense => expense.CancellationAuthorizedByUserId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasOne<AuthorizationRequest>()
                .WithMany()
                .HasForeignKey(expense => expense.AuthorizationRequestId)
                .OnDelete(DeleteBehavior.Restrict);

            // Correlativo unico del vale (VCC-000001).
            entity.HasIndex(expense => expense.VoucherNumber)
                .IsUnique()
                .HasDatabaseName("IX_PettyCashExpenses_VoucherNumber");

            entity.HasIndex(expense => expense.SessionId)
                .HasDatabaseName("IX_PettyCashExpenses_SessionId");

            // Arqueo/cierre: gastos vigentes de la sesion (el renglon OPEX solo suma Active).
            entity.HasIndex(expense => new { expense.SessionId, expense.Status })
                .HasDatabaseName("IX_PettyCashExpenses_SessionId_Status");

            entity.HasIndex(expense => expense.CreatedByUserId)
                .HasDatabaseName("IX_PettyCashExpenses_CreatedByUserId");

            // 8.202-H4 (S8): ventana del Cierre Z por fecha de registro del gasto.
            entity.HasIndex(expense => expense.CreatedAtUtc)
                .HasDatabaseName("IX_PettyCashExpenses_CreatedAtUtc");

            entity.HasIndex(expense => expense.AuthorizedByUserId)
                .HasDatabaseName("IX_PettyCashExpenses_AuthorizedByUserId");

            entity.HasIndex(expense => expense.CancelledByUserId)
                .HasDatabaseName("IX_PettyCashExpenses_CancelledByUserId");

            entity.HasIndex(expense => expense.CancellationAuthorizedByUserId)
                .HasDatabaseName("IX_PettyCashExpenses_CancellationAuthorizedByUserId");

            entity.HasIndex(expense => expense.AuthorizationRequestId)
                .HasDatabaseName("IX_PettyCashExpenses_AuthorizationRequestId");

            entity.Property(expense => expense.AmountBsS).HasColumnType("decimal(18,2)");
            entity.Property(expense => expense.AmountUsd).HasColumnType("decimal(18,4)");
            entity.Property(expense => expense.HistoricalRate).HasColumnType("decimal(18,4)");

            entity.Property(expense => expense.Concept).IsRequired().HasMaxLength(500);
            entity.Property(expense => expense.Beneficiary).IsRequired().HasMaxLength(150);
            entity.Property(expense => expense.ReferenceNumber).IsRequired().HasMaxLength(100);
            entity.Property(expense => expense.CancellationReason).HasMaxLength(500);

            // T2a (S3): idempotencia del registro; unica solo cuando la clave existe
            // (NULL multiple permitido), mismo contrato de ReceivablePayments/CreditReturns.
            entity.Property(expense => expense.IdempotencyKey).HasMaxLength(100);
            entity.HasIndex(expense => expense.IdempotencyKey)
                .IsUnique()
                .HasFilter("\"IdempotencyKey\" IS NOT NULL")
                .HasDatabaseName("IX_PettyCashExpenses_IdempotencyKey");
        });
    }
}
