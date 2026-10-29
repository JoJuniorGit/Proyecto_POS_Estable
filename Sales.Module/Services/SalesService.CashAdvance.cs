using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sales.Module.Entities;

namespace Sales.Module.Services;

public partial class SalesService
{
    public async Task<SaleDto> CreateCashAdvanceSaleAsync(
        decimal requestedAmountLocal,
        decimal commissionAmountLocal,
        int paymentMethodId,
        string paymentMethodName,
        bool isTransfer,
        decimal exchangeRate,
        int? cashierId = null,
        string? userName = null,
        IDbContextTransaction? existingTransaction = null,
        System.Threading.CancellationToken cancellationToken = default)
    {
        if (existingTransaction != null && _context.Database.ProviderName != null && !_context.Database.ProviderName.Contains("InMemory"))
        {
            var dbTx = existingTransaction.GetDbTransaction();
            if (dbTx != null)
            {
                _context.Database.UseTransaction(dbTx);
            }
        }

        // 8.211-T3a/T3c3b2 (S6/S7/S8): resolver perfil y método ANTES de cualquier efecto
        // (producto de sistema, nextval de factura, usuario o cliente). Un rechazo de perfil o
        // moneda no debe consumir un consecutivo ni dejar trabajo parcial.
        var monetaryProfile = await ResolveMonetaryProfileSnapshotAsync(cancellationToken);
        EnsureSupportedCheckoutMonetaryProfile(monetaryProfile, allowSingle: false);

        var paymentMethod = await _context.PaymentMethods
            .AsNoTracking()
            .FirstOrDefaultAsync(pm => pm.Id == paymentMethodId, cancellationToken);
        if (paymentMethod is not null)
        {
            EnsurePaymentMethodCurrencyMatchesProfile(monetaryProfile, paymentMethod);
        }

        // 1. Obtener o auto-garantizar el ID del producto especial IsCashAdvance
        int productId = 1;
        if (_inventoryService != null)
        {
            try
            {
                var p = await _inventoryService.GetCashAdvanceProductAsync(cancellationToken);

                if (p != null)
                {
                    productId = p.Id;
                }
                else
                {
                    productId = await _inventoryService.CreateSystemProductAsync(new CreateSystemProductRequest
                    {
                        Name = "Adelanto de Efectivo",
                        SKU = "ADV-001",
                        PriceRetailUSD = 0m,
                        StockQuantity = 999999,
                        IsCashAdvance = true,
                        IsActive = true
                    }, cancellationToken);
                }
            }
            catch (KeyNotFoundException)
            {
                // Cash advance product not found by lookup — safe to fall through to id=1 as last resort.
                productId = 1;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // DB or concurrency failure: propagate instead of silently imputting to product id=1.
                throw new InvalidOperationException($"No se pudo obtener el producto de adelanto de efectivo. Detalle: {ex.Message}", ex);
            }
        }

        // 2. Resolver usuario / cajero
        int? resolvedCashierId = cashierId;
        if (!resolvedCashierId.HasValue && !string.IsNullOrWhiteSpace(userName))
        {
            var matchedUser = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Name == userName || u.FullName == userName || u.Cedula == userName, cancellationToken);
            resolvedCashierId = matchedUser?.Id;
        }

        if (!resolvedCashierId.HasValue)
        {
            var defaultUser = await _context.Users.AsNoTracking().FirstOrDefaultAsync(u => u.IsActive, cancellationToken);
            resolvedCashierId = defaultUser?.Id;
        }

        // 3. Obtener cliente por defecto (cacheado; PERF-09)
        CustomerDto? defaultCustomer = null;
        try
        {
            defaultCustomer = await _customers.GetDefaultCustomerAsync(cancellationToken);
        }
        catch (KeyNotFoundException)
        {
            // Se conserva el fallback histórico para instalaciones sin cliente sembrado.
        }

        int customerId = defaultCustomer?.Id ?? 1;
        string customerName = defaultCustomer?.Name ?? "CLIENTE CONTADO";
        string customerCedula = defaultCustomer?.CedulaOrRif ?? "V-00000000";

        // 4. Consecutivo de Facturación atómico en transacción
        int nextInvoice = await GenerateNextInvoiceNumberAsync();

        // 8.5-A5 (residual): el adelanto de efectivo ancla su tasa a la BCV del día con la misma
        // política que CompleteSale/HoldSale (desvío > tolerancia => ancla; >= ±100% => rechazo).
        decimal anchoredRate = await ResolveAnchoredRateAsync(exchangeRate, contextLabel: "CashAdvance", referenceId: nextInvoice);

        decimal totalChargedLocal = requestedAmountLocal + commissionAmountLocal;
        decimal totalChargedUSD = anchoredRate > 0 ? Math.Round(totalChargedLocal / anchoredRate, 4) : 0m;

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
            AppliedRate = anchoredRate,
            Subtotal = totalChargedUSD,
            TotalUSD = totalChargedUSD,
            SubtotalBsS = totalChargedLocal,
            TotalBsS = totalChargedLocal,
            FinalPaidAmountBsS = totalChargedLocal,
            // 8.211-T3c2 (S6): snapshots aditivos de importes del adelanto completado directo.
            // 8.211-T3c3c1 (S4): la fuente de tasa sigue el régimen (Bcv/Manual; Identity en Single).
            PrimaryAmount = totalChargedUSD,
            SecondaryAmount = totalChargedLocal,
            AppliedRateSource = ResolveAppliedRateSource(monetaryProfile),
            PrimaryCurrencyCode = monetaryProfile.PrimaryCurrencyCode,
            SecondaryCurrencyCode = monetaryProfile.SecondaryCurrencyCode,
            MonetaryMode = monetaryProfile.MonetaryMode
        };

        _context.Sales.Add(sale);
        await _context.SaveChangesAsync(cancellationToken);

        // 6. Crear SaleItem asignando explícitamente el precio unitario y subtotal (sobreescribiendo precio base 0)
        var saleItem = new SaleItem
        {
            SaleId = sale.Id,
            ProductId = productId,
            ProductName = $"Adelanto de Efectivo ({paymentMethodName})",
            Quantity = 1m,
            UnitCostUSD = 0m,
            UnitPrice = totalChargedUSD,
            Subtotal = totalChargedUSD,
            UnitPriceBsS = totalChargedLocal,
            SubtotalBsS = totalChargedLocal
        };

        _context.SaleItems.Add(saleItem);

        // 7. Crear SalePayment a nombre del método electrónico
        // 8.211-T3b1 (S6/S8): la moneda snapshot sale del método persistido; el nombre recibido
        // solo resuelve el fallback legacy cuando la fila no está disponible.
        string currencyCode = paymentMethod is null
            ? PaymentMethodCurrencyResolver.Resolve(paymentMethodName)
            : ResolvePaymentMethodCurrency(paymentMethod);

        var payment = new SalePayment
        {
            SaleId = sale.Id,
            PaymentMethodId = paymentMethodId,
            Amount = totalChargedUSD,
            AmountBsS = totalChargedLocal,
            ExchangeRate = anchoredRate,
            CreatedAt = DateTime.UtcNow,
            ReferenceNumber = $"ADELANTO-{DateTime.UtcNow:yyyyMMddHHmmss}",
            CurrencyCode = currencyCode,
            OriginalAmount = ResolveOriginalAmount(totalChargedUSD, totalChargedLocal, currencyCode)
        };

        _context.SalePayments.Add(payment);
        await _context.SaveChangesAsync(cancellationToken);

        return MapToDto(sale);
    }
}
