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
    public DbSet<StockMovementArchive> StockMovements_Archive { get; set; }
    public DbSet<StockReservation> StockReservations { get; set; }
    public DbSet<SystemSetting> SystemSettings { get; set; }
    public DbSet<ExchangeRateHistory> ExchangeRateHistory { get; set; }
    public DbSet<Supplier> Suppliers { get; set; }
    public DbSet<SupplierColumnMapping> SupplierColumnMappings { get; set; }
    public DbSet<SupplierProductCode> SupplierProductCodes { get; set; }
    public DbSet<SupplierInvoice> SupplierInvoices { get; set; }
    public DbSet<SupplierInvoiceLine> SupplierInvoiceLines { get; set; }



    private static UnitOfMeasureType ParseUnitOfMeasure(string v)
    {
        return Enum.TryParse<UnitOfMeasureType>(v, true, out var result) ? result : UnitOfMeasureType.Und;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Product>().Property(p => p.Name).IsRequired().HasMaxLength(200);
        modelBuilder.Entity<Product>().Property(p => p.SKU).IsRequired().HasMaxLength(50);
        modelBuilder.Entity<Product>().Property(p => p.GroupKey).HasMaxLength(50);
        modelBuilder.Entity<Product>(entity =>
        {
            entity.HasIndex(p => p.SKU)
                .IsUnique()
                .HasFilter("\"IsDeleted\" = false")
                .HasDatabaseName("IX_Products_SKU");

            entity.HasIndex(p => p.Name)
                .HasDatabaseName("IX_Products_Name");

            entity.HasIndex(p => new { p.IsActive, p.IsDeleted, p.Name })
                .HasDatabaseName("IX_Products_Active_Deleted_Name")
                .HasFilter("\"IsActive\" = true AND \"IsDeleted\" = false");

            entity.HasOne(p => p.ParentProduct)
                .WithMany(p => p.Variants)
                .HasForeignKey(p => p.ParentProductId)
                .OnDelete(DeleteBehavior.Restrict);

            entity.HasIndex(p => new { p.ParentProductId, p.IsActive, p.IsDeleted })
                .HasDatabaseName("IX_Products_Parent_Active_Deleted")
                .HasFilter("\"ParentProductId\" IS NOT NULL");

            entity.HasIndex(p => new { p.IsGroupHeader, p.IsActive, p.IsDeleted, p.Name })
                .HasDatabaseName("IX_Products_GroupActiveName");

            entity.Property(p => p.IsStockShared).HasDefaultValue(false);
            entity.Property(p => p.HasIndependentPricing).HasDefaultValue(false);
            entity.Property(p => p.ConversionFactor).HasPrecision(18, 4).HasDefaultValue(1.0000m);

            entity.ToTable(t => {
                t.HasCheckConstraint("CK_Products_Variant_Flags",
                    "(\"IsGroupHeader\" = TRUE AND \"ParentProductId\" IS NULL) OR (\"IsStockShared\" = FALSE AND \"HasIndependentPricing\" = FALSE)");
                t.HasCheckConstraint("CK_Products_ConversionFactor", "\"ConversionFactor\" > 0");
            });

            entity.HasIndex(p => p.GroupKey)
                .HasDatabaseName("IX_Products_GroupKey")
                .HasFilter("\"GroupKey\" IS NOT NULL AND \"IsDeleted\" = false");
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
        // 8.9-M9: el archiver recorre por MovementDate (StockMovementArchiverJob).
        modelBuilder.Entity<StockMovement>().HasIndex(m => m.MovementDate).HasDatabaseName("IX_StockMovements_MovementDate");
        // 8.16-H03: lookup de idempotencia por SaleId (dedupe de deducción, handler de inventario).
        modelBuilder.Entity<StockMovement>().HasIndex(m => m.SaleId).HasDatabaseName("IX_StockMovements_SaleId");

        modelBuilder.Entity<StockMovementArchive>(entity =>
        {
            entity.HasKey(m => m.Id);
            entity.ToTable("StockMovements_Archive");
            entity.Property(m => m.QuantityChange).HasColumnType("numeric(18,3)").HasPrecision(18, 3);
            entity.Property(m => m.NewStockLevel).HasColumnType("numeric(18,3)").HasPrecision(18, 3);
            entity.HasIndex(m => m.OriginalMovementId).HasDatabaseName("IX_StockMovements_Archive_OriginalMovementId");
            entity.HasIndex(m => m.MovementDate).HasDatabaseName("IX_StockMovements_Archive_MovementDate");
        });

        // Token de concurrencia basado en la pseudo-columna de sistema `xmin` de PostgreSQL
        // (hallazgo 8.2-A1). Se configura en caliente sin DDL adicional; se omite en SQLite.
        if (Database.ProviderName != "Microsoft.EntityFrameworkCore.Sqlite")
        {
            modelBuilder.Entity<Product>()
                .Property<uint>("xmin")
                .HasColumnType("xid")
                .ValueGeneratedOnAddOrUpdate()
                .IsConcurrencyToken();
        }

        modelBuilder.Entity<StockReservation>().HasKey(r => r.Id);
        modelBuilder.Entity<StockReservation>().HasOne(r => r.Product).WithMany().HasForeignKey(r => r.ProductId);
        modelBuilder.Entity<StockReservation>().HasOne(r => r.SourceProduct).WithMany().HasForeignKey(r => r.SourceProductId).OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<StockReservation>().Property(r => r.Quantity).HasColumnType("numeric(18,3)").HasPrecision(18, 3);
        modelBuilder.Entity<StockReservation>()
            .HasIndex(r => new { r.ExpiryDate, r.IsConfirmed })
            .HasDatabaseName("IX_StockReservations_ExpiryDate_IsConfirmed");
        // 8.9-M9: tope de reservas por usuario/referencia (ReservationsController).
        modelBuilder.Entity<StockReservation>()
            .HasIndex(r => r.ReferenceId)
            .HasDatabaseName("IX_StockReservations_ReferenceId");

        // SystemSetting: Key-value store for app configuration
        modelBuilder.Entity<SystemSetting>(entity =>
        {
            entity.HasKey(s => s.Key);
            entity.Property(s => s.Key).HasMaxLength(100);
            entity.Property(s => s.Value).HasMaxLength(500);
        });

        // ExchangeRateHistory: One record per day, UNIQUE on Date
        // 8.142: la PK ya provee el indice unico sobre Date; el HasIndex explicito duplicaba
        // estructura y agregaba costo de escritura en cada upsert de tasa.
        modelBuilder.Entity<ExchangeRateHistory>(entity =>
        {
            entity.HasKey(e => e.Date);
            entity.Property(e => e.Rate).HasPrecision(18, 4);
        });

        modelBuilder.Entity<Supplier>(entity =>
        {
            entity.Property(s => s.RifOrNit).HasMaxLength(50);
            entity.Property(s => s.NormalizedRifOrNit).HasMaxLength(50);
            entity.Property(s => s.CommercialName).IsRequired().HasMaxLength(200);
            entity.Property(s => s.NormalizedCommercialName).IsRequired().HasMaxLength(200);
            entity.HasIndex(s => s.NormalizedRifOrNit)
                .IsUnique()
                .HasFilter("\"NormalizedRifOrNit\" IS NOT NULL AND \"NormalizedRifOrNit\" <> ''")
                .HasDatabaseName("IX_Suppliers_NormalizedRifOrNit");
            entity.HasIndex(s => s.NormalizedCommercialName)
                .HasDatabaseName("IX_Suppliers_NormalizedCommercialName");
        });

        modelBuilder.Entity<SupplierColumnMapping>(entity =>
        {
            entity.Property(m => m.NameColumnName).IsRequired();
            entity.Property(m => m.QuantityColumnName).IsRequired();
            entity.Property(m => m.UnitCostColumnName).IsRequired();
            entity.HasIndex(m => m.SupplierId)
                .IsUnique()
                .HasDatabaseName("IX_SupplierColumnMappings_SupplierId");
            entity.HasOne(m => m.Supplier)
                .WithOne(s => s.ColumnMapping)
                .HasForeignKey<SupplierColumnMapping>(m => m.SupplierId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SupplierProductCode>(entity =>
        {
            entity.HasIndex(c => new { c.SupplierId, c.Code })
                .IsUnique()
                .HasFilter("\"Code\" IS NOT NULL")
                .HasDatabaseName("IX_SupplierProductCodes_Supplier_Code");
            entity.HasOne(c => c.Supplier)
                .WithMany(s => s.ProductCodes)
                .HasForeignKey(c => c.SupplierId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(c => c.Product)
                .WithMany()
                .HasForeignKey(c => c.ProductId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SupplierInvoice>(entity =>
        {
            entity.Property(i => i.Status)
                .HasConversion<string>()
                .HasMaxLength(32)
                .HasDefaultValue(SupplierInvoiceStatus.Draft);
            entity.HasOne(i => i.Supplier)
                .WithMany(s => s.Invoices)
                .HasForeignKey(i => i.SupplierId)
                .OnDelete(DeleteBehavior.Restrict);

            if (Database.ProviderName != "Microsoft.EntityFrameworkCore.Sqlite")
            {
                entity.Property<uint>("xmin")
                    .HasColumnType("xid")
                    .ValueGeneratedOnAddOrUpdate()
                    .IsConcurrencyToken();
            }
        });

        modelBuilder.Entity<SupplierInvoiceLine>(entity =>
        {
            entity.Property(line => line.SupplierCode).HasMaxLength(100);
            entity.Property(line => line.Barcode).HasMaxLength(100);
            entity.Property(line => line.Name).HasMaxLength(200);
            entity.Property(line => line.Status)
                .HasConversion<string>()
                .HasMaxLength(32)
                .HasDefaultValue(SupplierInvoiceLineStatus.New);
            entity.Property(line => line.MatchMethod)
                .HasConversion<string>()
                .HasMaxLength(32)
                .HasDefaultValue(MatchMethod.None);
            entity.Property(line => line.IsApproved).HasDefaultValue(true);
            entity.Property(line => line.Quantity).HasColumnType("numeric(18,3)").HasPrecision(18, 3);
            entity.Property(line => line.UnitCostUSD).HasColumnType("numeric(18,2)").HasPrecision(18, 2);
            entity.Property(line => line.OldCostPriceUSD).HasColumnType("numeric(18,2)").HasPrecision(18, 2);
            entity.Property(line => line.OldProfitMarginRetail).HasColumnType("numeric(18,2)").HasPrecision(18, 2);
            entity.Property(line => line.OldProfitMarginWholesale).HasColumnType("numeric(18,2)").HasPrecision(18, 2);
            entity.Property(line => line.OldStockQuantity).HasColumnType("numeric(18,3)").HasPrecision(18, 3);
            entity.Property(line => line.MarginRetailOverride).HasColumnType("numeric(18,2)").HasPrecision(18, 2);
            entity.Property(line => line.MarginWholesaleOverride).HasColumnType("numeric(18,2)").HasPrecision(18, 2);
            entity.Property(line => line.SuggestedRetailPriceUSD).HasColumnType("numeric(18,2)").HasPrecision(18, 2);
            entity.Property(line => line.SuggestedWholesalePriceUSD).HasColumnType("numeric(18,2)").HasPrecision(18, 2);
            entity.HasOne(line => line.SupplierInvoice)
                .WithMany(invoice => invoice.Lines)
                .HasForeignKey(line => line.SupplierInvoiceId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(line => line.ResolvedProduct)
                .WithMany()
                .HasForeignKey(line => line.ResolvedProductId)
                .OnDelete(DeleteBehavior.SetNull);
        });
    }
}
