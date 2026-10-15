using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Sales.Module.Entities;

namespace Sales.Module.Services;

public partial class SalesService
{
    public async Task<SaleDto> UpdateExchangeRateAsync(int saleId, decimal exchangeRate, int? actingUserId = null, System.Threading.CancellationToken cancellationToken = default)
    {
        var sale = await GetSaleEntityAsync(saleId);
        EnsureHoldClaimAccess(sale, actingUserId);
        if (sale.Status != SaleStatus.Pending && sale.Status != SaleStatus.OnHold) 
            throw new InvalidOperationException("No se puede modificar una venta ya finalizada.");

        sale.AppliedRate = await ResolveAnchoredRateAsync(exchangeRate, contextLabel: "UpdateExchangeRate", referenceId: sale.Id);
        await RecalculateTotalAsync(sale);
        await _context.SaveChangesAsync(cancellationToken);
        return MapToDto(sale);
    }

    /// <inheritdoc />
    public async Task<int> RecalculateOnHoldSalesAsync(decimal newExchangeRate, System.Threading.CancellationToken cancellationToken = default)
    {
        if (newExchangeRate <= 0)
            return 0;

        // 8.9-B5: procesamiento por lotes (paginado) para no cargar todo el conjunto OnHold en
        // memoria; cada página se graba al terminar. El recálculo unitario (RecalculateTotalAsync)
        // ya usa batch fetch de productos, de modo que el costo por página se mantiene acotado.
        // 8.16-H14: paginación por KEYSET (WHERE Id > último) en lugar de Skip/Take: la fila
        // filtrada no cambia su Id durante el recalculo (solo AppliedRate/totales), de modo que
        // el keyset es estable y evita el re-escaneo Offset del Skip en cada página.
        const int batchSize = 200;
        int totalUpdated = 0;
        int lastId = 0;

        while (true)
        {
            List<Sale> batch;
            int batchCount;

            try
            {
                batch = await _context.Sales
                    .AsSplitQuery()
                    .Include(s => s.Items)
                    .Where(s => s.Status == SaleStatus.OnHold && s.Id > lastId)
                    .OrderBy(s => s.Id)
                    .Take(batchSize)
                    .ToListAsync(cancellationToken);

                if (batch.Count == 0)
                    break;

                Dictionary<int, SaleProductInfoDto>? batchProducts = null;
                if (_inventoryService != null)
                {
                    var batchProductIds = batch
                        .Where(s => s.Items != null)
                        .SelectMany(s => s.Items)
                        .Select(i => i.ProductId)
                        .Distinct()
                        .ToList();
                    if (batchProductIds.Count > 0)
                    {
                        var fetched = await _inventoryService.GetSaleProductsByIdsAsync(batchProductIds, cancellationToken);
                        if (fetched != null && fetched.Count > 0)
                        {
                            batchProducts = fetched.ToDictionary(p => p.Id);
                        }
                    }
                }

                var roundedRate = PricingCalculator.RoundExchangeRateCeiling(newExchangeRate);
                foreach (var sale in batch)
                {
                    sale.AppliedRate = roundedRate;
                    await RecalculateTotalAsync(sale, batchProducts);
                }

                batchCount = batch.Count;
                lastId = batch[batchCount - 1].Id;

                await _context.SaveChangesAsync(cancellationToken);
                _context.ChangeTracker.Clear();
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[RecalculateOnHold] Fallo al recalcular una página de ventas OnHold; {TotalUpdated} ventas ya fueron actualizadas y persistidas con la nueva tasa.", totalUpdated);
                throw;
            }

            totalUpdated += batchCount;

            _logger?.LogInformation("[RecalculateOnHold] Página recalculada: {PageCount} ventas OnHold (acumulado: {TotalUpdated}).", batchCount, totalUpdated);
        }

        _logger?.LogInformation("[RecalculateOnHold] Recálculo completado: {TotalUpdated} ventas OnHold actualizadas con la nueva tasa.", totalUpdated);

        return totalUpdated;
    }

    public async Task<SaleDto> UpdatePriceListAsync(int saleId, string priceListType, int? actingUserId = null, System.Threading.CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(priceListType) || (priceListType != "Retail" && priceListType != "Wholesale"))
        {
            throw new ArgumentException("Tipo de lista de precios no válido. Debe ser 'Retail' o 'Wholesale'.");
        }

        var sale = await _context.Sales
            .AsSplitQuery()
            .Include(s => s.Items)
            .Include(s => s.Payments)
                .ThenInclude(p => p.PaymentMethod)
            .Include(s => s.Customer)
            .FirstOrDefaultAsync(s => s.Id == saleId, cancellationToken);

        if (sale == null)
        {
            throw new KeyNotFoundException($"Venta #{saleId} no encontrada.");
        }

        EnsureHoldClaimAccess(sale, actingUserId);

        if (sale.Status == SaleStatus.Completed)
        {
            throw new InvalidOperationException("No se puede modificar la lista de precios de una venta ya finalizada.");
        }

        sale.PriceListType = priceListType;
        await RecalculateTotalAsync(sale);

        if (sale.Status == SaleStatus.OnHold && sale.Payments.Any())
        {
            decimal totalPaidUsd = sale.Payments.Sum(p => p.Amount);
            if (sale.TotalUSD < totalPaidUsd)
            {
                throw new ArgumentException("No se puede cambiar la lista de precios: el nuevo total en USD es menor al monto ya abonado por el cliente.");
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return MapToDto(sale);
    }

    private async Task RecalculateTotalAsync(Sale sale, Dictionary<int, SaleProductInfoDto>? preloadedProducts = null)
    {
        if (sale.Items != null && sale.Items.Any())
        {
            var products = preloadedProducts ?? new Dictionary<int, SaleProductInfoDto>();

            if (preloadedProducts == null && _inventoryService != null)
            {
                var productIds = sale.Items.Select(i => i.ProductId).Distinct().ToList();
                var fetched = await _inventoryService.GetSaleProductsByIdsAsync(productIds);
                if (fetched != null && fetched.Count > 0)
                {
                    products = fetched.ToDictionary(p => p.Id);
                }
            }

            foreach (var item in sale.Items)
            {
                // Regla 1: Lookup O(1) desde el batch fetch
                products.TryGetValue(item.ProductId, out var product);
                if (product != null)
                {
                    item.IsFractional = product.IsFractional;
                    item.UnitOfMeasure = product.UnitOfMeasure;

                    // Regla 2: Fallback anti-precio cero y umbral mayorista
                    var wholesalePrice = (product.PriceWholesaleUSD > 0) 
                        ? product.PriceWholesaleUSD 
                        : (product.PriceRetailUSD > 0 ? product.PriceRetailUSD : product.PriceUSD);
                    var retailPrice = (product.PriceRetailUSD > 0) ? product.PriceRetailUSD : product.PriceUSD;

                    var minWholesaleQty = product.MinWholesaleQuantity > 0 ? product.MinWholesaleQuantity : 6m;

                    if (item.IsCustomPrice)
                    {
                        item.IsWholesaleApplied = false;
                    }
                    else if (string.Equals(sale.PriceListType, "Wholesale", StringComparison.OrdinalIgnoreCase) && product.HasWholesale && item.Quantity >= minWholesaleQty)
                    {
                        item.UnitPrice = wholesalePrice;
                        item.IsWholesaleApplied = true;
                    }
                    else
                    {
                        item.UnitPrice = retailPrice;
                        item.IsWholesaleApplied = false;
                    }
                }
                else if (item.UnitPrice == 0)
                {
                    throw new KeyNotFoundException($"Producto #{item.ProductId} no encontrado en la base de datos.");
                }

                item.UnitPriceBsS = PricingCalculator.ToBsSCeiling(item.UnitPrice, sale.AppliedRate);
                item.Subtotal = Math.Round(item.Quantity * item.UnitPrice, 4, MidpointRounding.AwayFromZero);
                item.SubtotalBsS = PricingCalculator.RoundToDigital(item.Quantity * item.UnitPriceBsS);
            }

            sale.Subtotal = Math.Round(sale.Items.Sum(i => i.Subtotal), 4, MidpointRounding.AwayFromZero);
            sale.SubtotalBsS = PricingCalculator.RoundToDigital(sale.Items.Sum(i => i.SubtotalBsS));

            sale.TotalUSD = Math.Round(sale.Subtotal, 2, MidpointRounding.AwayFromZero);
            sale.TotalBsS = PricingCalculator.RoundToDigital(sale.SubtotalBsS);
        }
        else if (sale.AppliedRate > 0)
        {
            sale.TotalBsS = PricingCalculator.RoundToDigital(sale.TotalUSD * sale.AppliedRate);
            sale.SubtotalBsS = sale.TotalBsS;
        }
    }

    private void ValidateHoldSaleTotal(Sale sale)
    {
        if (sale.Status == SaleStatus.OnHold)
        {
            decimal totalPaidUsd = sale.Payments.Sum(p => p.Amount);
            if (sale.TotalUSD < totalPaidUsd)
            {
                throw new ArgumentException($"El nuevo total de la venta (${sale.TotalUSD:F2}) no puede ser menor al monto que ya ha sido abonado por el cliente (${totalPaidUsd:F2}).");
            }
        }
    }
}
