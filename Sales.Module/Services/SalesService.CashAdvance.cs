using System;
using System.Threading.Tasks;
using Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sales.Module.Entities;

namespace Sales.Module.Services;

public partial class SalesService
{
    public async Task<Sale> CreateCashAdvanceSaleAsync(
        decimal requestedAmountLocal,
        decimal commissionAmountLocal,
        int paymentMethodId,
        string paymentMethodName,
        bool isTransfer,
        decimal exchangeRate,
        int? cashierId = null,
        string? userName = null,
        IDbContextTransaction? existingTransaction = null)
    {
        if (existingTransaction != null && _context.Database.ProviderName != null && !_context.Database.ProviderName.Contains("InMemory"))
        {
            var dbTx = existingTransaction.GetDbTransaction();
            if (dbTx != null)
            {
                _context.Database.UseTransaction(dbTx);
            }
        }

        // 1. Obtener o auto-garantizar el ID del producto especial IsCashAdvance
        int productId = 1;
        if (_inventoryService != null)
        {
            try
            {
                var p = await _inventoryService.GetCashAdvanceProductAsync();

                if (p != null)
                {
                    productId = p.Id;
                }
                else
                {
                    var newP = await _inventoryService.CreateProductAsync(new Product
                    {
                        Name = "Adelanto de Efectivo",
                        SKU = "ADV-001",
                        PriceRetailUSD = 0m,
                        StockQuantity = 999999,
                        IsCashAdvance = true,
                        IsActive = true
                    });
                    productId = newP.Id;
                }
            }
            catch
            {
                productId = 1;
            }
        }

        // 2. Resolver usuario / cajero
        int? resolvedCashierId = cashierId;
        if (!resolvedCashierId.HasValue && !string.IsNullOrWhiteSpace(userName))
        {
            var matchedUser = await _context.Users.FirstOrDefaultAsync(u => u.Name == userName || u.FullName == userName || u.Cedula == userName);
            resolvedCashierId = matchedUser?.Id;
        }

        if (!resolvedCashierId.HasValue)
        {
            var defaultUser = await _context.Users.FirstOrDefaultAsync(u => u.IsActive);
            resolvedCashierId = defaultUser?.Id;
        }

        // 3. Obtener cliente por defecto
        var defaultCustomer = await _context.Customers.FirstOrDefaultAsync(c => c.IsDefault)
                           ?? await _context.Customers.FirstOrDefaultAsync(c => c.Id == 1);

        int customerId = defaultCustomer?.Id ?? 1;
        string customerName = defaultCustomer?.Name ?? "CLIENTE CONTADO";
        string customerCedula = defaultCustomer?.CedulaOrRif ?? "V-00000000";

        // 4. Consecutivo de Facturación atómico en transacción
        int nextInvoice = await GenerateNextInvoiceNumberAsync();

        decimal totalChargedLocal = requestedAmountLocal + commissionAmountLocal;
        decimal totalChargedUSD = exchangeRate > 0 ? Math.Round(totalChargedLocal / exchangeRate, 4) : 0m;

        // 5. Crear Sale completado con el CashierId resuelto
        var sale = new Sale
        {
            Date = DateTime.UtcNow,
            Status = SaleStatus.Completed,
            DeliveryStatus = SaleDeliveryStatus.Delivered,
            InvoiceNumber = nextInvoice,
            CashierId = resolvedCashierId,
            CustomerId = customerId,
            CustomerName = customerName,
            CustomerCedula = customerCedula,
            AppliedRate = exchangeRate,
            Subtotal = totalChargedUSD,
            TotalUSD = totalChargedUSD,
            SubtotalBsS = totalChargedLocal,
            TotalBsS = totalChargedLocal,
            FinalPaidAmountBsS = totalChargedLocal
        };

        _context.Sales.Add(sale);
        await _context.SaveChangesAsync();

        // 6. Crear SaleItem asignando explícitamente el precio unitario y subtotal (sobreescribiendo precio base 0)
        var saleItem = new SaleItem
        {
            SaleId = sale.Id,
            ProductId = productId,
            ProductName = $"Adelanto de Efectivo ({paymentMethodName})",
            Quantity = 1m,
            UnitPrice = totalChargedUSD,
            Subtotal = totalChargedUSD,
            UnitPriceBsS = totalChargedLocal,
            SubtotalBsS = totalChargedLocal
        };

        _context.SaleItems.Add(saleItem);

        // 7. Crear SalePayment a nombre del método electrónico
        var payment = new SalePayment
        {
            SaleId = sale.Id,
            PaymentMethodId = paymentMethodId,
            Amount = totalChargedUSD,
            AmountBsS = totalChargedLocal,
            ExchangeRate = exchangeRate,
            CreatedAt = DateTime.UtcNow,
            ReferenceNumber = $"ADELANTO-{DateTime.UtcNow:yyyyMMddHHmmss}"
        };

        _context.SalePayments.Add(payment);
        await _context.SaveChangesAsync();

        return sale;
    }
}
