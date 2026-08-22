using Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Module.Data;

public class InventoryDbContext : DbContext
{
    public InventoryDbContext(DbContextOptions<InventoryDbContext> options) : base(options)
    {
    }

    public DbSet<Product> Products { get; set; }
    public DbSet<StockMovement> StockMovements { get; set; }
    public DbSet<StockReservation> StockReservations { get; set; }
    public DbSet<SystemSetting> SystemSettings { get; set; }
    public DbSet<ExchangeRateHistory> ExchangeRateHistory { get; set; }



    private static UnitOfMeasureType ParseUnitOfMeasure(string v)
    {
        return Enum.TryParse<UnitOfMeasureType>(v, true, out var result) ? result : UnitOfMeasureType.Und;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Product>().Property(p => p.Name).IsRequired().HasMaxLength(200);
        modelBuilder.Entity<Product>().Property(p => p.SKU).IsRequired().HasMaxLength(50);
        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasIndex(p => p.SKU)
                .IsUnique()
                .HasFilter("\"IsDeleted\" = false");

            entity.HasIndex(p => new { p.IsActive, p.IsDeleted });
        });

        // Precision and conversion configurations
        modelBuilder.Entity<Product>()
            .Property(p => p.UnitOfMeasure)
            .HasConversion(
                v => v.ToString(),
                v => ParseUnitOfMeasure(v)
            );

        modelBuilder.Entity<Product>().Property(p => p.PriceUSD).HasPrecision(18, 2);
        modelBuilder.Entity<Product>().Property(p => p.PriceRetailUSD).HasPrecision(18, 2);
        modelBuilder.Entity<Product>().Property(p => p.PriceWholesaleUSD).HasPrecision(18, 2);
        modelBuilder.Entity<Product>().Property(p => p.CostPriceUSD).HasPrecision(18, 2);
        modelBuilder.Entity<Product>().Property(p => p.ProfitMarginRetail).HasPrecision(18, 2);
        modelBuilder.Entity<Product>().Property(p => p.ProfitMarginWholesale).HasPrecision(18, 2);
        modelBuilder.Entity<Product>().Property(p => p.MinWholesaleQuantity).HasColumnType("numeric(18,3)").HasPrecision(18, 3);
        modelBuilder.Entity<Product>().Property(p => p.Cost).HasPrecision(18, 2);
        modelBuilder.Entity<Product>().Property(p => p.ProfitPercentage).HasPrecision(18, 2);
        modelBuilder.Entity<Product>().Property(p => p.StockQuantity).HasColumnType("numeric(18,3)").HasPrecision(18, 3);
        modelBuilder.Entity<Product>().Property(p => p.ReservedQuantity).HasColumnType("numeric(18,3)").HasPrecision(18, 3);
        modelBuilder.Entity<Product>().Property(p => p.LowStockThreshold).HasColumnType("numeric(18,3)").HasPrecision(18, 3);
        modelBuilder.Entity<Product>().Property(p => p.PriceBsS).HasPrecision(18, 2);
        modelBuilder.Entity<Product>().Property(p => p.LastConversionRate).HasPrecision(18, 4);

        modelBuilder.Entity<StockMovement>().HasKey(m => m.Id);
        modelBuilder.Entity<StockMovement>().HasOne(m => m.Product).WithMany().HasForeignKey(m => m.ProductId);
        modelBuilder.Entity<StockMovement>().Property(m => m.QuantityChange).HasColumnType("numeric(18,3)").HasPrecision(18, 3);
        modelBuilder.Entity<StockMovement>().Property(m => m.NewStockLevel).HasColumnType("numeric(18,3)").HasPrecision(18, 3);

        modelBuilder.Entity<Product>().Property(p => p.RowVersion).IsRowVersion();

        modelBuilder.Entity<StockReservation>().HasKey(r => r.Id);
        modelBuilder.Entity<StockReservation>().HasOne(r => r.Product).WithMany().HasForeignKey(r => r.ProductId);
        modelBuilder.Entity<StockReservation>().Property(r => r.Quantity).HasColumnType("numeric(18,3)").HasPrecision(18, 3);

        // SystemSetting: Key-value store for app configuration
        modelBuilder.Entity<SystemSetting>(entity =>
        {
            entity.HasKey(s => s.Key);
            entity.Property(s => s.Key).HasMaxLength(100);
            entity.Property(s => s.Value).HasMaxLength(500);
        });

        // ExchangeRateHistory: One record per day, UNIQUE on Date
        modelBuilder.Entity<ExchangeRateHistory>(entity =>
        {
            entity.HasKey(e => e.Date);
            entity.HasIndex(e => e.Date).IsUnique();
            entity.Property(e => e.Rate).HasPrecision(18, 4);
        });
    }
}
