using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Constants;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Module.Services;

/// <summary>
/// 8.180-T1 (S2-S4): sesiones de conteo físico ciego (Paso 6 del roadmap).
/// Reglas centrales:
/// - CIEGO: detalle/scan/set SOLO exponen producto + cantidad contada; teórico y costo viven
///   exclusivamente en <see cref="ReviewAsync"/> (Admin/Manager).
/// - Alcance: si la sesión tiene <c>ScopeCategoryId</c>, el producto debe pertenecer al subárbol
///   de esa categoría (BFS en memoria, provider-agnóstico).
/// - Guards de conteo (fail-fast en el scan/set): producto borrado, adelanto de efectivo, agrupador
///   individual y variante con stock compartido no se cuentan.
/// - Ciclo de vida: scan/set/remove solo en Open; Close congela teórico + costo; Apply (solo desde
///   Closed) ajusta el stock con Δ = contado − actual en UNA transacción junto al estado Applied
///   (guard xmin); Cancel desde Open/Closed.
/// </summary>
public class StockCountService : IStockCountService
{
    private const int MaxNameLength = 80;
    private const int MaxNotesLength = 300;

    private readonly InventoryDbContext _context;
    private readonly IInventoryService _inventoryService;
    private readonly ICurrentUserService? _currentUserService;

    public StockCountService(
        InventoryDbContext context,
        IInventoryService inventoryService,
        ICurrentUserService? currentUserService = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(inventoryService);
        _context = context;
        _inventoryService = inventoryService;
        _currentUserService = currentUserService;
    }

    public async Task<StockCountSessionDto> CreateAsync(CreateStockCountRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var name = (request.Name ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            throw new ArgumentException("El nombre de la sesión de conteo no puede estar vacío.", nameof(request));
        }
        if (name.Length > MaxNameLength)
        {
            throw new ArgumentException($"El nombre de la sesión de conteo no puede exceder los {MaxNameLength} caracteres.", nameof(request));
        }

        string? notes = request.Notes?.Trim();
        if (notes is { Length: 0 })
        {
            notes = null;
        }
        if (notes is not null && notes.Length > MaxNotesLength)
        {
            throw new ArgumentException($"Las notas de la sesión de conteo no pueden exceder los {MaxNotesLength} caracteres.", nameof(request));
        }

        if (request.ScopeCategoryId.HasValue
            && !await _context.Categories.AsNoTracking().AnyAsync(c => c.Id == request.ScopeCategoryId.Value, cancellationToken))
        {
            throw new KeyNotFoundException($"Categoría con ID {request.ScopeCategoryId.Value} no encontrada.");
        }

        var session = new StockCountSession
        {
            Name = name,
            Status = StockCountStatus.Open,
            ScopeCategoryId = request.ScopeCategoryId,
            CreatedByUserId = _currentUserService?.UserId,
            Notes = notes
        };

        _context.StockCountSessions.Add(session);
        await _context.SaveChangesAsync(cancellationToken);

        return await BuildSessionDtoAsync(session.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<StockCountSessionDto>> ListAsync(StockCountStatus? status = null, CancellationToken cancellationToken = default)
    {
        IQueryable<StockCountSession> query = _context.StockCountSessions.AsNoTracking();
        if (status.HasValue)
        {
            query = query.Where(s => s.Status == status.Value);
        }

        var sessions = await query
            .OrderByDescending(s => s.CreatedAt)
            .ThenByDescending(s => s.Id)
            .ToListAsync(cancellationToken);

        var itemCounts = await _context.StockCountItems.AsNoTracking()
            .GroupBy(i => i.StockCountSessionId)
            .Select(g => new { SessionId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SessionId, x => x.Count, cancellationToken);

        var categories = await _context.Categories.AsNoTracking().ToListAsync(cancellationToken);
        var byId = categories.ToDictionary(c => c.Id);

        return sessions
            .Select(s => ToDto(
                s,
                itemCounts.TryGetValue(s.Id, out var count) ? count : 0,
                ResolveScopePath(s.ScopeCategoryId, byId)))
            .ToList();
    }

    public async Task<StockCountDetailDto> GetDetailAsync(int id, CancellationToken cancellationToken = default)
    {
        var session = await _context.StockCountSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Sesión de conteo {id} no encontrada.");

        // 8.180-T1 (S3): detalle CIEGO — el ítem no incluye teórico ni costo.
        var items = await _context.StockCountItems.AsNoTracking()
            .Where(i => i.StockCountSessionId == id)
            .OrderBy(i => i.Product.Name)
            .ThenBy(i => i.ProductId)
            .Select(i => new StockCountItemDto(
                i.ProductId,
                i.Product.SKU,
                i.Product.Name,
                i.CountedQuantity,
                i.UpdatedAt ?? i.CreatedAt))
            .ToListAsync(cancellationToken);

        var path = await ResolveScopePathAsync(session.ScopeCategoryId, cancellationToken);
        return new StockCountDetailDto(ToDto(session, items.Count, path), items);
    }

    public async Task<StockCountItemDto> ScanAsync(int id, ScanStockCountRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var session = await GetOpenTrackedSessionAsync(id, cancellationToken);

        var code = (request.Code ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            throw new ArgumentException("El código escaneado no puede estar vacío.", nameof(request));
        }

        var product = await FindProductBySkuAsync(code, cancellationToken)
            ?? throw new KeyNotFoundException($"Código no encontrado: '{code}'. Verifique el SKU o código de barras.");

        ValidateCountableProduct(product);
        await EnsureProductInScopeAsync(session, product.Id, cancellationToken);

        var item = await _context.StockCountItems
            .FirstOrDefaultAsync(i => i.StockCountSessionId == id && i.ProductId == product.Id, cancellationToken);
        if (item is null)
        {
            item = new StockCountItem { StockCountSessionId = id, ProductId = product.Id };
            _context.StockCountItems.Add(item);
        }

        // El conteo nunca queda negativo: un decremento por debajo de 0 se ancla en 0.
        item.CountedQuantity = Math.Max(0m, item.CountedQuantity + request.QuantityDelta);
        item.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return new StockCountItemDto(product.Id, product.SKU, product.Name, item.CountedQuantity, item.UpdatedAt.Value);
    }

    public async Task<StockCountItemDto> SetCountedQuantityAsync(int id, int productId, SetCountedQuantityRequestDto request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.CountedQuantity < 0)
        {
            throw new ArgumentException("La cantidad contada no puede ser negativa.", nameof(request));
        }

        var session = await GetOpenTrackedSessionAsync(id, cancellationToken);

        var product = await _context.Products.AsNoTracking()
            .Include(p => p.ParentProduct)
            .FirstOrDefaultAsync(p => p.Id == productId, cancellationToken)
            ?? throw new KeyNotFoundException($"Producto {productId} no encontrado.");

        ValidateCountableProduct(product);
        await EnsureProductInScopeAsync(session, product.Id, cancellationToken);

        var item = await _context.StockCountItems
            .FirstOrDefaultAsync(i => i.StockCountSessionId == id && i.ProductId == productId, cancellationToken);
        if (item is null)
        {
            item = new StockCountItem
            {
                StockCountSessionId = id,
                ProductId = productId,
                CountedQuantity = request.CountedQuantity,
                UpdatedAt = DateTime.UtcNow
            };
            _context.StockCountItems.Add(item);
        }
        else
        {
            item.CountedQuantity = request.CountedQuantity;
            item.UpdatedAt = DateTime.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return new StockCountItemDto(product.Id, product.SKU, product.Name, item.CountedQuantity, item.UpdatedAt.Value);
    }

    public async Task RemoveItemAsync(int id, int productId, CancellationToken cancellationToken = default)
    {
        // Solo una sesión Open admite edición (misma regla que scan/set).
        await GetOpenTrackedSessionAsync(id, cancellationToken);

        var item = await _context.StockCountItems
            .FirstOrDefaultAsync(i => i.StockCountSessionId == id && i.ProductId == productId, cancellationToken)
            ?? throw new KeyNotFoundException($"El producto {productId} no está en la sesión de conteo #{id}.");

        _context.StockCountItems.Remove(item);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task<StockCountReviewDto> ReviewAsync(int id, CancellationToken cancellationToken = default)
    {
        var session = await _context.StockCountSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Sesión de conteo {id} no encontrada.");

        // Open = teórico LIVE (preview); Closed/Applied/Cancelled = congelado al cierre.
        var isPreview = session.Status == StockCountStatus.Open;

        var rows = await _context.StockCountItems.AsNoTracking()
            .Where(i => i.StockCountSessionId == id)
            .OrderBy(i => i.Product.Name)
            .ThenBy(i => i.ProductId)
            .Select(i => new
            {
                i.ProductId,
                i.Product.SKU,
                i.Product.Name,
                i.CountedQuantity,
                i.TheoreticalStock,
                i.UnitCostUSD,
                LiveStock = i.Product.StockQuantity,
                LiveCost = i.Product.CostPriceUSD
            })
            .ToListAsync(cancellationToken);

        var reviewItems = rows.Select(row =>
        {
            var theoretical = isPreview ? row.LiveStock : row.TheoreticalStock ?? 0m;
            var cost = isPreview ? row.LiveCost : row.UnitCostUSD ?? 0m;
            var difference = row.CountedQuantity - theoretical;
            // Redondeo fiscal: 2 decimales, AwayFromZero (regla de integridad financiera).
            var differenceValue = Math.Round(difference * cost, 2, MidpointRounding.AwayFromZero);
            return new StockCountReviewItemDto(
                row.ProductId, row.SKU, row.Name, row.CountedQuantity, theoretical, difference, cost, differenceValue, isPreview);
        }).ToList();

        decimal surplusQuantity = 0m, shortageQuantity = 0m, surplusValue = 0m, shortageValue = 0m;
        foreach (var item in reviewItems)
        {
            if (item.Difference > 0)
            {
                surplusQuantity += item.Difference;
                surplusValue += item.DifferenceValueUsd;
            }
            else if (item.Difference < 0)
            {
                shortageQuantity += -item.Difference;
                shortageValue += -item.DifferenceValueUsd;
            }
        }

        var unscanned = await CountUnscannedInScopeAsync(session.ScopeCategoryId, reviewItems.Count, cancellationToken);

        return new StockCountReviewDto(
            session.Id,
            session.Status,
            isPreview,
            reviewItems,
            surplusQuantity,
            shortageQuantity,
            surplusValue,
            shortageValue,
            surplusValue - shortageValue,
            unscanned);
    }

    public async Task<StockCountSessionDto> CloseAsync(int id, CancellationToken cancellationToken = default)
    {
        var session = await _context.StockCountSessions
            .Include(s => s.Items)
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Sesión de conteo {id} no encontrada.");

        if (session.Status != StockCountStatus.Open)
        {
            throw new InvalidOperationException($"La sesión de conteo #{id} no está abierta (estado actual: {session.Status}).");
        }

        await FreezeSessionItemsAsync(session, cancellationToken);
        session.Status = StockCountStatus.Closed;
        session.ClosedAt = DateTime.UtcNow;
        session.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return await BuildSessionDtoAsync(id, cancellationToken);
    }

    public async Task<StockCountSessionDto> ApplyAsync(int id, CancellationToken cancellationToken = default)
    {
        var appliedByUserId = _currentUserService?.UserId;

        // 8.180-T1 (S4): estado Applied + ajustes de stock en UNA transacción. El molde es
        // UpdateStockBatchAsync: la transacción manual vive DENTRO de CreateExecutionStrategy para
        // no romper NpgsqlRetryingExecutionStrategy en producción.
        await _context.Database.CreateExecutionStrategy().ExecuteAsync(async _ =>
        {
            // a) Carga TRACKEADA: el token xmin de la sesión corta un apply concurrente.
            var session = await _context.StockCountSessions
                .Include(s => s.Items)
                .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
                ?? throw new KeyNotFoundException($"Sesión de conteo {id} no encontrada.");

            if (session.Status != StockCountStatus.Closed)
            {
                throw new InvalidOperationException(session.Status == StockCountStatus.Applied
                    ? $"La sesión de conteo #{id} ya fue aplicada."
                    : $"La sesión de conteo #{id} debe estar cerrada para aplicarse (estado actual: {session.Status}).");
            }

            bool hasExistingTx = _context.Database.CurrentTransaction != null;
            await using var tx = (!hasExistingTx && _context.Database.IsRelational())
                ? await _context.Database.BeginTransactionAsync(cancellationToken)
                : null;

            // b) Pre-validación estricta de TODOS los productos escaneados: si algo no es contable,
            // se aborta sin tocar stock ni estado (la transacción revierte todo).
            var productIds = session.Items.Select(i => i.ProductId).Distinct().ToList();
            var products = await _context.Products.AsNoTracking()
                .Include(p => p.ParentProduct)
                .Where(p => productIds.Contains(p.Id))
                .ToListAsync(cancellationToken);
            var productById = products.ToDictionary(p => p.Id);

            var validationErrors = new List<string>();
            foreach (var item in session.Items)
            {
                if (!productById.TryGetValue(item.ProductId, out var product))
                {
                    validationErrors.Add($"Producto {item.ProductId} no encontrado.");
                    continue;
                }

                if (product.IsDeleted)
                {
                    validationErrors.Add($"'{product.Name}' (SKU {product.SKU}): {InventoryMessages.DeletedProductStockAdjustmentBlocked}");
                }
                else if (product.IsCashAdvance)
                {
                    validationErrors.Add($"'{product.Name}': {InventoryMessages.CashAdvanceStockAdjustmentBlocked}");
                }
                else if (product.IsGroupHeader && !product.IsStockShared)
                {
                    validationErrors.Add($"'{product.Name}': {InventoryMessages.GroupIndividualStockAdjustmentBlocked}");
                }
                else if (product.ParentProductId.HasValue && product.ParentProduct is not null && product.ParentProduct.IsStockShared)
                {
                    validationErrors.Add($"'{product.Name}': {InventoryMessages.VariantSharedStockAdjustmentBlocked}");
                }
            }

            if (validationErrors.Count > 0)
            {
                throw new InvalidOperationException(
                    $"No se puede aplicar la sesión de conteo #{id}: {string.Join(" | ", validationErrors)}");
            }

            // c) Δ = contado − stock actual (el stock queda EXACTAMENTE en lo contado); Δ=0 se omite.
            var requests = new List<StockDeductionRequest>();
            foreach (var item in session.Items)
            {
                var currentStock = productById[item.ProductId].StockQuantity;
                var difference = item.CountedQuantity - currentStock;
                if (difference == 0m)
                {
                    continue;
                }

                requests.Add(new StockDeductionRequest(
                    item.ProductId,
                    difference,
                    $"Toma física #{session.Id}: {session.Name}",
                    SaleId: null,
                    OriginType: StockMovementOriginType.Manual,
                    ReasonCode: StockMovementReason.PhysicalCountCorrection,
                    AuthorizedByUserId: appliedByUserId,
                    SupplierInvoiceId: null,
                    Observation: $"Toma física #C-{session.Id} {session.Name}",
                    StockCountSessionId: session.Id));
            }

            // d) Claim del estado con guard xmin: si otro apply ganó la carrera → 409.
            session.Status = StockCountStatus.Applied;
            session.AppliedAt = DateTime.UtcNow;
            session.AppliedByUserId = appliedByUserId;
            session.UpdatedAt = DateTime.UtcNow;
            try
            {
                await _context.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                throw new InvalidOperationException($"Otra operación está aplicando la sesión de conteo #{id}.");
            }
            catch (DbUpdateException)
            {
                // 8.180-T1 (fix verificación): un fallo de persistencia genérico no es necesariamente
                // una carrera de apply; el mensaje no debe inducir a reintentar a ciegas.
                throw new InvalidOperationException($"No se pudo aplicar la sesión de conteo #{id}. Verifique el estado de la sesión e intente nuevamente.");
            }

            // e) El batch comparte la transacción AMBIENTE (mismo DbContext scoped; hasExistingTx).
            if (requests.Count > 0)
            {
                await _inventoryService.UpdateStockBatchAsync(requests, cancellationToken: cancellationToken);
            }

            // f) Commit único: estado + movimientos.
            if (tx is not null)
            {
                await tx.CommitAsync(cancellationToken);
            }
        }, cancellationToken);

        return await BuildSessionDtoAsync(id, cancellationToken);
    }

    public async Task<StockCountSessionDto> CancelAsync(int id, CancellationToken cancellationToken = default)
    {
        var session = await _context.StockCountSessions
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Sesión de conteo {id} no encontrada.");

        if (session.Status is StockCountStatus.Applied or StockCountStatus.Cancelled)
        {
            throw new InvalidOperationException($"La sesión de conteo #{id} no puede cancelarse en su estado actual ({session.Status}).");
        }

        session.Status = StockCountStatus.Cancelled;
        session.UpdatedAt = DateTime.UtcNow;
        await _context.SaveChangesAsync(cancellationToken);

        return await BuildSessionDtoAsync(id, cancellationToken);
    }

    /// <summary>
    /// Congela existencia teórica y costo unitario por ítem con los valores VIGENTES al cerrar.
    /// Esos snapshots son la referencia absoluta del descuadre y del apply (inmutables después).
    /// </summary>
    private async Task FreezeSessionItemsAsync(StockCountSession session, CancellationToken cancellationToken)
    {
        var productIds = session.Items.Select(i => i.ProductId).Distinct().ToList();
        var products = await _context.Products.AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.StockQuantity, p.CostPriceUSD })
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        foreach (var item in session.Items)
        {
            if (!products.TryGetValue(item.ProductId, out var product))
            {
                throw new KeyNotFoundException($"Producto {item.ProductId} no encontrado al cerrar la sesión de conteo.");
            }

            item.TheoreticalStock = product.StockQuantity;
            item.UnitCostUSD = product.CostPriceUSD;
            item.UpdatedAt = DateTime.UtcNow;
        }
    }

    private async Task<StockCountSession> GetOpenTrackedSessionAsync(int id, CancellationToken cancellationToken)
    {
        var session = await _context.StockCountSessions
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Sesión de conteo {id} no encontrada.");

        if (session.Status != StockCountStatus.Open)
        {
            throw new InvalidOperationException($"La sesión de conteo #{id} no está abierta (estado actual: {session.Status}).");
        }

        return session;
    }

    /// <summary>
    /// Busca por SKU exacto (case-sensitive, ya trimmeado; el SKU es el código de barras del repo).
    /// Se prioriza el producto vigente: si el único match está borrado, se devuelve para que el
    /// guard de conteo lo rechace con un 409 claro (en vez de un 404 confuso).
    /// </summary>
    private async Task<Product?> FindProductBySkuAsync(string code, CancellationToken cancellationToken)
    {
        var active = await _context.Products.AsNoTracking()
            .Include(p => p.ParentProduct)
            .FirstOrDefaultAsync(p => p.SKU == code && !p.IsDeleted, cancellationToken);
        if (active is not null)
        {
            return active;
        }

        return await _context.Products.AsNoTracking()
            .Include(p => p.ParentProduct)
            .FirstOrDefaultAsync(p => p.SKU == code, cancellationToken);
    }

    private static void ValidateCountableProduct(Product product)
    {
        if (product.IsDeleted)
        {
            throw new InvalidOperationException(InventoryMessages.DeletedProductStockAdjustmentBlocked);
        }
        if (product.IsCashAdvance)
        {
            throw new InvalidOperationException(InventoryMessages.CashAdvanceStockAdjustmentBlocked);
        }
        if (product.IsGroupHeader && !product.IsStockShared)
        {
            throw new InvalidOperationException(InventoryMessages.GroupIndividualStockAdjustmentBlocked);
        }
        if (product.ParentProductId.HasValue && product.ParentProduct is not null && product.ParentProduct.IsStockShared)
        {
            throw new InvalidOperationException(InventoryMessages.VariantSharedStockAdjustmentBlocked);
        }
    }

    private async Task EnsureProductInScopeAsync(StockCountSession session, int productId, CancellationToken cancellationToken)
    {
        if (session.ScopeCategoryId is not int scopeCategoryId)
        {
            return;
        }

        var subtreeIds = await ResolveCategorySubtreeIdsAsync(scopeCategoryId, cancellationToken);
        var inScope = await _context.ProductCategories.AsNoTracking()
            .AnyAsync(pc => pc.ProductId == productId && subtreeIds.Contains(pc.CategoryId), cancellationToken);

        if (!inScope)
        {
            throw new InvalidOperationException($"El producto no pertenece al alcance de la sesión de conteo #{session.Id}.");
        }
    }

    /// <summary>
    /// 8.180-T1: set categoría + descendientes calculado en memoria (BFS, molde CatalogQueries)
    /// para ser provider-agnóstico (InMemory/SQLite/PostgreSQL) sin CTE recursivo.
    /// </summary>
    private async Task<List<int>> ResolveCategorySubtreeIdsAsync(int categoryId, CancellationToken cancellationToken)
    {
        var nodes = await _context.Categories.AsNoTracking()
            .Select(c => new { c.Id, c.ParentCategoryId })
            .ToListAsync(cancellationToken);

        if (!nodes.Any(n => n.Id == categoryId))
        {
            return new List<int>();
        }

        var subtreeIds = new HashSet<int> { categoryId };
        var queue = new Queue<int>();
        queue.Enqueue(categoryId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var child in nodes.Where(n => n.ParentCategoryId == current))
            {
                if (subtreeIds.Add(child.Id))
                {
                    queue.Enqueue(child.Id);
                }
            }
        }

        return subtreeIds.ToList();
    }

    /// <summary>
    /// Productos CONTABLES del alcance que no fueron escaneados: activos, no borrados, sin adelanto
    /// de efectivo, sin agrupador individual y sin variantes de stock compartido (esos no pueden
    /// contarse, por lo que no deben inflar el pendiente). Supuesto L2: los no escaneados no se aplican.
    /// </summary>
    private async Task<int> CountUnscannedInScopeAsync(int? scopeCategoryId, int scannedCount, CancellationToken cancellationToken)
    {
        IQueryable<Product> query = _context.Products.AsNoTracking()
            .Where(p => p.IsActive && !p.IsDeleted && !p.IsCashAdvance)
            .Where(p => !(p.IsGroupHeader && !p.IsStockShared))
            .Where(p => p.ParentProduct == null || !p.ParentProduct.IsStockShared);

        if (scopeCategoryId is int scopeId)
        {
            var subtreeIds = await ResolveCategorySubtreeIdsAsync(scopeId, cancellationToken);
            var inScopeProductIds = _context.ProductCategories.AsNoTracking()
                .Where(pc => subtreeIds.Contains(pc.CategoryId))
                .Select(pc => pc.ProductId);
            query = query.Where(p => inScopeProductIds.Contains(p.Id));
        }

        var total = await query.CountAsync(cancellationToken);
        return Math.Max(0, total - scannedCount);
    }

    private async Task<string?> ResolveScopePathAsync(int? categoryId, CancellationToken cancellationToken)
    {
        if (categoryId is not int id)
        {
            return null;
        }

        var categories = await _context.Categories.AsNoTracking().ToListAsync(cancellationToken);
        return ResolveScopePath(categoryId, categories.ToDictionary(c => c.Id));
    }

    private static string? ResolveScopePath(int? categoryId, IReadOnlyDictionary<int, Category> byId)
    {
        if (categoryId is not int id || !byId.TryGetValue(id, out var category))
        {
            return null;
        }

        return CategoryHierarchy.Describe(category, byId).Path;
    }

    private async Task<StockCountSessionDto> BuildSessionDtoAsync(int id, CancellationToken cancellationToken)
    {
        var session = await _context.StockCountSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken)
            ?? throw new KeyNotFoundException($"Sesión de conteo {id} no encontrada.");

        var itemsCount = await _context.StockCountItems.AsNoTracking()
            .CountAsync(i => i.StockCountSessionId == id, cancellationToken);
        var path = await ResolveScopePathAsync(session.ScopeCategoryId, cancellationToken);

        return ToDto(session, itemsCount, path);
    }

    private static StockCountSessionDto ToDto(StockCountSession session, int itemsCount, string? scopeCategoryPath) => new(
        session.Id,
        session.Name,
        session.Status,
        session.ScopeCategoryId,
        scopeCategoryPath,
        itemsCount,
        session.CreatedByUserId,
        session.CreatedAt,
        session.ClosedAt,
        session.AppliedAt);
}
