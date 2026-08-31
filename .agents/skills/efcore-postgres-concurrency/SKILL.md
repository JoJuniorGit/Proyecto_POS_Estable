---
name: efcore-postgres-concurrency
description: >-
  Governs PostgreSQL EF Core database patterns with Npgsql, focusing on optimistic concurrency control
  (xmin concurrency tokens, stock deduction race conditions), strict DTO data pipeline isolation,
  resilient connection retry strategies, AsNoTracking query optimization, and efficient pagination.
  Activate this skill when modifying database entities, migrations, sales repository transactions,
  inventory stock adjustments, or database performance tuning.
---

# PostgreSQL EF Core & Concurrency Control Guide

This skill governs all database operations using `Npgsql.EntityFrameworkCore.PostgreSQL` (.NET 10), ensuring transactional consistency, high-speed non-blocking sales, and zero stock overselling.

---

## 1. Optimistic Concurrency with PostgreSQL `xmin`

PostgreSQL maintains a system column `xmin` (the transaction ID that created/updated the row) which EF Core Npgsql maps as a row-version concurrency token.

### A. Entity Definition (C#)
```csharp
namespace Core.Entities;

public class Product
{
    public int Id { get; set; }
    public string SKU { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal CostPriceUSD { get; set; }
    public decimal ProfitMargin { get; set; }
    public decimal PriceUSD { get; set; }
    public decimal PriceWholesaleUSD { get; set; }
    public decimal StockQuantity { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>
    /// Concurrency Token mapped to PostgreSQL xmin system column.
    /// Changes on every row UPDATE automatically in PostgreSQL.
    /// </summary>
    public uint Version { get; set; }
}
```

### B. DbContext Fluent API Configuration (`OnModelCreating`)
```csharp
using Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Infrastructure.Data;

public class AppDbContext : DbContext
{
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Sale> Sales => Set<Sale>();

    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Map PostgreSQL xmin system column as row version concurrency token
        modelBuilder.Entity<Product>()
            .Property(p => p.Version)
            .IsRowVersion(); // In Npgsql, IsRowVersion() automatically uses xmin

        // Configure indexes for high-speed POS search
        modelBuilder.Entity<Product>()
            .HasIndex(p => p.SKU)
            .IsUnique();

        modelBuilder.Entity<Product>()
            .HasIndex(p => p.Name);
    }
}
```

### C. Concurrency-Safe Stock Deduction with Retry Loop
```csharp
namespace Inventory.Module.Services;

public class StockService : IStockService
{
    private readonly AppDbContext _dbContext;

    public StockService(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<bool> DeductStockAsync(int productId, decimal quantity, CancellationToken cancellationToken, int maxRetries = 3)
    {
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                var product = await _dbContext.Products.FindAsync(new object[] { productId }, cancellationToken)
                    ?? throw new KeyNotFoundException($"Producto ID {productId} no encontrado.");

                if (product.StockQuantity < quantity)
                {
                    throw new InvalidOperationException($"Stock insuficiente para {product.SKU}. Disponible: {product.StockQuantity}, Requerido: {quantity}");
                }

                product.StockQuantity -= quantity;
                await _dbContext.SaveChangesAsync(cancellationToken);
                return true;
            }
            catch (DbUpdateConcurrencyException) when (attempt < maxRetries)
            {
                // Another cashier updated this product concurrently; delay and retry with fresh entity
                await Task.Delay(50 * attempt, cancellationToken);
            }
        }

        throw new DbUpdateConcurrencyException($"No se pudo deducir inventario del producto {productId} tras {maxRetries} reintentos por alta concurrencia.");
    }
}
```

---

## 2. High-Performance Query Rules: Split Queries & Caching

### A. Preventing Cartesian Explosion with `.AsSplitQuery()`
When loading complex sales with multiple child collections (`Items` + `Payments`), ALWAYS use `AsSplitQuery()`:

```csharp
public async Task<SaleDetailDto?> GetSaleByIdAsync(int saleId, CancellationToken cancellationToken)
{
    var sale = await _dbContext.Sales
        .AsNoTracking()
        .AsSplitQuery() // Executes 2-3 clean queries instead of 1 massive cartesian product JOIN
        .Include(s => s.Items)
        .Include(s => s.Payments)
        .Include(s => s.Customer)
        .FirstOrDefaultAsync(s => s.Id == saleId, cancellationToken);

    return sale is null ? null : MapToDetailDto(sale);
}
```

### B. In-Memory Caching (`IMemoryCache`) for Read-Heavy Static Data
```csharp
public async Task<IReadOnlyList<PaymentMethodDto>> GetActivePaymentMethodsAsync(CancellationToken cancellationToken)
{
    const string cacheKey = "active_payment_methods";
    
    if (!_memoryCache.TryGetValue(cacheKey, out IReadOnlyList<PaymentMethodDto>? methods))
    {
        methods = await _dbContext.PaymentMethods
            .AsNoTracking()
            .Where(m => m.IsActive)
            .OrderBy(m => m.DisplayOrder)
            .Select(m => new PaymentMethodDto(m.Id, m.Code, m.Name, m.Currency, m.CommissionPercentage))
            .ToListAsync(cancellationToken);

        var cacheOptions = new MemoryCacheEntryOptions()
            .SetSlidingExpiration(TimeSpan.FromMinutes(30))
            .SetAbsoluteExpiration(TimeSpan.FromHours(4));

        _memoryCache.Set(cacheKey, methods, cacheOptions);
    }

    return methods ?? Array.Empty<PaymentMethodDto>();
}
```

---

## 3. Mandatory Data Pipeline & DTO Isolation

* **Strict Rule**: Never expose EF Core entities to controllers or views.
  $$\text{Database} \longrightarrow \text{EF Entity} \longrightarrow \text{Service Layer} \longrightarrow \text{DTO} \longrightarrow \text{Controller / UI}$$

### Read-Only Query with `AsNoTracking()` and Paged Response
```csharp
public async Task<PagedResultDto<ProductQuickInfoDto>> GetCatalogPageAsync(
    int page, 
    int pageSize, 
    string? searchTerm, 
    CancellationToken cancellationToken)
{
    var query = _dbContext.Products.AsNoTracking().Where(p => p.IsActive);

    if (!string.IsNullOrWhiteSpace(searchTerm))
    {
        query = query.Where(p => EF.Functions.ILike(p.Name, $"%{searchTerm}%") || EF.Functions.ILike(p.SKU, $"%{searchTerm}%"));
    }

    int totalCount = await query.CountAsync(cancellationToken);
    
    var items = await query
        .OrderBy(p => p.Name)
        .Skip((page - 1) * pageSize)
        .Take(pageSize)
        .Select(p => new ProductQuickInfoDto
        {
            Id = p.Id,
            SKU = p.SKU,
            Name = p.Name,
            PriceUSD = p.PriceUSD,
            PriceWholesaleUSD = p.PriceWholesaleUSD,
            StockQuantity = p.StockQuantity
        })
        .ToListAsync(cancellationToken);

    bool hasMore = (page * pageSize) < totalCount;
    return new PagedResultDto<ProductQuickInfoDto>(items, totalCount, hasMore);
}
```

---

## 4. Self-Evaluation Concurrency Test Recipe

Run database and inventory concurrency tests:
```powershell
dotnet test CommandCenter.Tests/CommandCenter.Tests.csproj --filter "FullyQualifiedName~Inventory|FullyQualifiedName~Repository"
```
