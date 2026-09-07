using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Entities;

namespace Sales.Module.Services;

public partial class SalesService
{
    public async Task<SaleDto> UpdateExchangeRateAsync(int sale_id, decimal exchange_rate)
    {
        var _sale = await GetSaleEntityAsync(sale_id);
        if (_sale.Status != SaleStatus.Pending && _sale.Status != SaleStatus.OnHold) 
            throw new InvalidOperationException("Cannot modify a completed sale.");

        _sale.AppliedRate = exchange_rate;
        await RecalculateTotalAsync(_sale);
        await _context.SaveChangesAsync();
        return MapToDto(_sale);
    }

    /// <inheritdoc />
    public async Task<int> RecalculateOnHoldSalesAsync(decimal newExchangeRate)
    {
        if (newExchangeRate <= 0)
            return 0;

        // 8.9-B5: procesamiento por lotes (paginado) para no cargar todo el conjunto OnHold en
        // memoria; cada página se graba al terminar. El recálculo unitario (RecalculateTotalAsync)
        // ya usa batch fetch de productos, de modo que el costo por página se mantiene acotado.
        const int batchSize = 200;
        int totalUpdated = 0;

        while (true)
        {
            var batch = await _context.Sales
                .AsSplitQuery()
                .Include(s => s.Items)
                .Include(s => s.Payments)
                .Where(s => s.Status == SaleStatus.OnHold)
                .OrderBy(s => s.Id)
                .Skip(totalUpdated)
                .Take(batchSize)
                .ToListAsync();

            if (batch.Count == 0)
                break;

            foreach (var sale in batch)
            {
                sale.AppliedRate = newExchangeRate;
                await RecalculateTotalAsync(sale);
            }

            await _context.SaveChangesAsync();
            totalUpdated += batch.Count;
        }

        return totalUpdated;
    }

    public async Task<SaleDto> UpdatePriceListAsync(int saleId, string priceListType)
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
            .FirstOrDefaultAsync(s => s.Id == saleId);

        if (sale == null)
        {
            throw new KeyNotFoundException($"Venta #{saleId} no encontrada.");
        }

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
                throw new InvalidOperationException("No se puede cambiar la lista de precios: el nuevo total en USD es menor al monto ya abonado por el cliente.");
            }
        }

        await _context.SaveChangesAsync();
        return MapToDto(sale);
    }

    private async Task RecalculateTotalAsync(Sale sale)
    {
        if (sale.Items != null && sale.Items.Any())
        {
            var productIds = sale.Items.Select(i => i.ProductId).Distinct().ToList();
            var products = new Dictionary<int, Product>();

            if (_inventoryService != null)
            {
                var fetched = await _inventoryService.GetProductsByIdsAsync(productIds);
                if (fetched != null && fetched.Count > 0)
                {
                    products = fetched.ToDictionary(p => p.Id);
                }
                else
                {
                    // Fallback para stubs/mocks en pruebas que únicamente configuran GetProductByIdAsync
                    foreach (var id in productIds)
                    {
                        var p = await _inventoryService.GetProductByIdAsync(id);
                        if (p != null)
                        {
                            products[p.Id] = p;
                        }
                    }
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

                    if (string.Equals(sale.PriceListType, "Wholesale", StringComparison.OrdinalIgnoreCase) && product.HasWholesale && item.Quantity >= minWholesaleQty)
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

                item.UnitPriceBsS = PricingCalculator.RoundToDigital(item.UnitPrice * sale.AppliedRate);
                item.Subtotal = Math.Round(item.Quantity * item.UnitPrice, 4, MidpointRounding.AwayFromZero);
                item.SubtotalBsS = PricingCalculator.RoundToDigital(item.Subtotal * sale.AppliedRate);
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
                throw new InvalidOperationException($"El nuevo total de la venta (${sale.TotalUSD:F2}) no puede ser menor al monto que ya ha sido abonado por el cliente (${totalPaidUsd:F2}).");
            }
        }
    }
}
