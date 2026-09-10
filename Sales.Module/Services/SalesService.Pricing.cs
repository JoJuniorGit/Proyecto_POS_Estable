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
    public async Task<SaleDto> UpdateExchangeRateAsync(int saleId, decimal exchangeRate)
    {
        var sale = await GetSaleEntityAsync(saleId);
        if (sale.Status != SaleStatus.Pending && sale.Status != SaleStatus.OnHold) 
            throw new InvalidOperationException("No se puede modificar una venta ya finalizada.");

        sale.AppliedRate = exchangeRate;
        await RecalculateTotalAsync(sale);
        await _context.SaveChangesAsync();
        return MapToDto(sale);
    }

    /// <inheritdoc />
    public async Task<int> RecalculateOnHoldSalesAsync(decimal newExchangeRate)
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
            var batch = await _context.Sales
                .AsSplitQuery()
                .Include(s => s.Items)
                .Include(s => s.Payments)
                .Where(s => s.Status == SaleStatus.OnHold && s.Id > lastId)
                .OrderBy(s => s.Id)
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
            lastId = batch[batch.Count - 1].Id;
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
