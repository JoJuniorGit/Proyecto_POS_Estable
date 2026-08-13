using Microsoft.EntityFrameworkCore;
using Sales.Module.Entities;
using Core.Entities;

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
    public DbSet<PaymentMethod> PaymentMethods { get; set; } = null!;
    public DbSet<SalePayment> SalePayments { get; set; } = null!;
    public DbSet<CashDrawerSession> CashDrawerSessions { get; set; } = null!;
    public DbSet<CashTransaction> CashTransactions { get; set; } = null!;
    public DbSet<DailyClosure> DailyClosures { get; set; } = null!;
    public DbSet<ClosureDetail> ClosureDetails { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Customer configuration
        modelBuilder.Entity<Customer>()
            .HasIndex(c => c.CedulaOrRif)
            .IsUnique();

        modelBuilder.Entity<Customer>()
            .Property(c => c.CreditLimitUSD)
            .HasColumnType("decimal(18,2)");

        // User configurations
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Cedula)
            .IsUnique();

        modelBuilder.Entity<User>().HasData(
            new User
            {
                Id = 1,
                Cedula = "12345678",
                Name = "Administrador",
                Username = "admin",
                Role = UserRole.Admin,
                IsActive = true,
                CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
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
            .HasColumnType("decimal(18,2)");

        modelBuilder.Entity<Sale>().Property(s => s.TotalBsS).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<Sale>().Property(s => s.SubtotalBsS).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<Sale>().Property(s => s.RoundingAdjustment).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<SaleItem>().Property(i => i.Quantity).HasColumnType("decimal(18,3)");
        modelBuilder.Entity<SaleItem>().Property(i => i.UnitPrice).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<SaleItem>().Property(i => i.Subtotal).HasColumnType("decimal(18,4)");

        modelBuilder.Entity<Sale>()
            .HasIndex(s => s.Date);

        modelBuilder.Entity<Sale>()
            .HasIndex(s => s.InvoiceNumber)
            .IsUnique()
            .HasFilter("\"InvoiceNumber\" IS NOT NULL");

        modelBuilder.Entity<SaleItem>().Property(i => i.UnitPriceBsS).HasColumnType("decimal(18,4)");
        modelBuilder.Entity<SaleItem>().Property(i => i.SubtotalBsS).HasColumnType("decimal(18,4)");

        modelBuilder.Entity<SalePayment>().Property(p => p.AmountBsS).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<SalePayment>().Property(p => p.ExchangeRate).HasColumnType("decimal(18,2)");

        // Cash Drawer Configurations
        modelBuilder.Entity<CashDrawerSession>()
            .HasMany(s => s.Transactions)
            .WithOne(t => t.Session)
            .HasForeignKey(t => t.SessionId)
            .OnDelete(DeleteBehavior.Restrict);

        modelBuilder.Entity<CashDrawerSession>().Property(s => s.OpeningBalanceLocal).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<CashDrawerSession>().Property(s => s.OpeningExchangeRate).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<CashDrawerSession>().Property(s => s.ClosingBalanceLocal).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<CashDrawerSession>().Property(s => s.ClosingExchangeRate).HasColumnType("decimal(18,2)");

        modelBuilder.Entity<CashTransaction>().Property(t => t.AmountUsd).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<CashTransaction>().Property(t => t.ExchangeRate).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<CashTransaction>().Property(t => t.AmountLocal).HasColumnType("decimal(18,2)");

        // Daily Closure Configurations
        modelBuilder.Entity<DailyClosure>()
            .HasMany(dc => dc.Details)
            .WithOne(d => d.DailyClosure)
            .HasForeignKey(d => d.DailyClosureId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DailyClosure>().Property(dc => dc.TotalExpectedBsS).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<DailyClosure>().Property(dc => dc.TotalActualBsS).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<DailyClosure>().Property(dc => dc.TotalDifferenceBsS).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<DailyClosure>().HasIndex(dc => dc.ClosureDate);

        modelBuilder.Entity<ClosureDetail>().Property(cd => cd.ExpectedAmountBsS).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<ClosureDetail>().Property(cd => cd.ActualAmountBsS).HasColumnType("decimal(18,2)");
        modelBuilder.Entity<ClosureDetail>().Property(cd => cd.DifferenceBsS).HasColumnType("decimal(18,2)");

        // Seed initial payment methods
        modelBuilder.Entity<PaymentMethod>().HasData(
            new PaymentMethod { Id = 1, Name = "Cash", IsActive = true, RequiresReference = false, IsCash = true },
            new PaymentMethod { Id = 2, Name = "Card", IsActive = true, RequiresReference = true, IsCash = false }
        );
    }
}
