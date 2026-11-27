using Core.DTOs;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

/// <summary>
/// Paso 15 (S1-S7): fachada del reporte de rotacion. Proyecta ventas emitidas por producto en SQL
/// y delega la clasificacion/paginacion a <see cref="InventoryRotationCalculator"/>. El catalogo
/// llega por <see cref="IInventoryService"/> para no acoplar Sales.Module a Inventory.Module.
/// </summary>
public sealed class InventoryRotationService : IInventoryRotationService
{
    private readonly SalesDbContext _context;
    private readonly IInventoryService _inventoryService;
    private readonly TimeProvider _timeProvider;

    public InventoryRotationService(
        SalesDbContext context,
        IInventoryService inventoryService,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(inventoryService);

        _context = context;
        _inventoryService = inventoryService;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<InventoryRotationReportDto> GetReportAsync(
        InventoryRotationFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        InventoryRotationCalculator.ValidateFilter(filter);

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var universe = await BuildUniverseAsync(filter, nowUtc, cancellationToken);
        return InventoryRotationCalculator.BuildReport(universe, filter, nowUtc);
    }

    public async Task<DeadStockSelectionDto> GetDeadStockProductIdsAsync(
        InventoryRotationFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        InventoryRotationCalculator.ValidateFilter(filter);

        var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
        var universe = await BuildUniverseAsync(filter, nowUtc, cancellationToken);
        return InventoryRotationCalculator.BuildDeadStockSelection(universe);
    }

    private async Task<InventoryRotationUniverse> BuildUniverseAsync(
        InventoryRotationFilterDto filter,
        DateTime nowUtc,
        CancellationToken cancellationToken)
    {
        IReadOnlyCollection<int>? productIds = null;
        if (filter.CategoryId.HasValue)
        {
            var categoryProductIds = await _inventoryService.GetProductIdsByCategoryAsync(
                new[] { filter.CategoryId.Value },
                filter.IncludeCategorySubtree,
                cancellationToken);

            if (categoryProductIds.Count == 0)
            {
                return BuildEmptyUniverse(filter, nowUtc);
            }

            productIds = categoryProductIds;
        }

        var products = await _inventoryService.GetInventoryRotationProductsAsync(
            productIds,
            filter.SearchText,
            cancellationToken);

        if (products.Count == 0)
        {
            return BuildEmptyUniverse(filter, nowUtc);
        }

        var productIdList = products.Select(product => product.ProductId).ToList();
        var fromUtc = nowUtc.AddDays(-filter.SalesWindowDays);

        var sales = await (
                from item in _context.SaleItems.AsNoTracking()
                join sale in _context.Sales.AsNoTracking() on item.SaleId equals sale.Id
                where productIdList.Contains(item.ProductId)
                    && sale.SaleType == SaleType.Merchandise
                    && sale.InvoiceNumber != null
                    && sale.Status == SaleStatus.Completed
                    && sale.CancelledAtUtc == null
                    && sale.Date >= fromUtc
                    && sale.Date < nowUtc
                group item by item.ProductId
                into grouped
                select new InventoryRotationSalesAggregateDto
                {
                    ProductId = grouped.Key,
                    UnitsSold = grouped.Sum(item => item.Quantity),
                    SalesRevenueUsd = grouped.Sum(item => item.Subtotal)
                })
            .ToListAsync(cancellationToken);

        // Ultima venta sin limite de ventana: un producto puede no vender en los ultimos 90 dias y
        // seguir teniendo una fecha real que explique su antiguedad comercial.
        var lastSales = await (
                from item in _context.SaleItems.AsNoTracking()
                join sale in _context.Sales.AsNoTracking() on item.SaleId equals sale.Id
                where productIdList.Contains(item.ProductId)
                    && sale.SaleType == SaleType.Merchandise
                    && sale.InvoiceNumber != null
                    && sale.Status == SaleStatus.Completed
                    && sale.CancelledAtUtc == null
                select new { item.ProductId, sale.Date })
            .GroupBy(row => row.ProductId)
            .Select(group => new InventoryRotationLastSaleDto
            {
                ProductId = group.Key,
                LastSaleDateUtc = group.Max(row => row.Date)
            })
            .ToListAsync(cancellationToken);

        return InventoryRotationCalculator.BuildUniverse(
            products,
            sales,
            lastSales,
            filter,
            nowUtc);
    }

    private static InventoryRotationUniverse BuildEmptyUniverse(
        InventoryRotationFilterDto filter,
        DateTime nowUtc)
    {
        return InventoryRotationCalculator.BuildUniverse(
            Array.Empty<InventoryRotationProductSnapshotDto>(),
            Array.Empty<InventoryRotationSalesAggregateDto>(),
            Array.Empty<InventoryRotationLastSaleDto>(),
            filter,
            nowUtc);
    }
}
