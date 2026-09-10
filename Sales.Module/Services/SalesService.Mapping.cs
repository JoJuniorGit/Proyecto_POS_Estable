using Core.DTOs;
using Core.Entities;
using Core.Events;
using Core.Interfaces;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace Sales.Module.Services;

public partial class SalesService
{

    private SaleDto MapToDto(Sale sale)
    {
        var totalPaidUsd = sale.Payments.Sum(p => p.Amount);
        var remainingBalanceUsd = Math.Max(0, sale.TotalUSD - totalPaidUsd);

        return new SaleDto
        {
            Id = sale.Id,
            InvoiceNumber = sale.InvoiceNumber,
            Date = sale.Date,
            Status = sale.Status.ToString(),
            Subtotal = sale.Subtotal,
            TotalUSD = sale.TotalUSD,
            AppliedRate = sale.AppliedRate,
            TotalBsS = sale.TotalBsS,
            FinalPaidAmountBsS = sale.FinalPaidAmountBsS,
            SubtotalBsS = sale.SubtotalBsS,
            CashierId = sale.CashierId,
            CashierName = sale.Cashier != null ? (string.IsNullOrWhiteSpace(sale.Cashier.Name) ? sale.Cashier.FullName : sale.Cashier.Name) : "Usuario Desconocido",
            CustomerName = sale.CustomerName,
            CustomerCedula = sale.CustomerCedula,
            DeliveryStatus = sale.DeliveryStatus.ToString(),
            PickupDate = sale.PickupDate,
            PriceListType = string.IsNullOrWhiteSpace(sale.PriceListType) ? "Retail" : sale.PriceListType,
            CustomerId = sale.CustomerId,
            Customer = sale.Customer != null ? new CustomerDto
            {
                Id = sale.Customer.Id,
                CedulaOrRif = sale.Customer.CedulaOrRif,
                Name = sale.Customer.Name,
                Phone = sale.Customer.Phone,
                CreditLimitUSD = sale.Customer.CreditLimitUSD,
                IsActive = sale.Customer.IsActive,
                IsDefault = sale.Customer.IsDefault
            } : null,
            TotalPaidUSD = totalPaidUsd,
            RemainingBalanceUSD = remainingBalanceUsd,
            Items = sale.Items.Select(i => new SaleItemDto
            {
                Id = i.Id,
                ProductId = i.ProductId,
                ProductName = i.ProductName,
                Quantity = i.Quantity,
                IsFractional = i.IsFractional,
                UnitOfMeasure = i.UnitOfMeasure,
                UnitPrice = i.UnitPrice,
                Subtotal = i.Subtotal,
                UnitPriceBsS = i.UnitPriceBsS,
                SubtotalBsS = i.SubtotalBsS,
                IsWholesaleApplied = i.IsWholesaleApplied,
                IsCustomPrice = i.IsCustomPrice
            }).ToList(),
            Payments = sale.Payments.Select(p => new SalePaymentDto
            {
                Id = p.Id,
                PaymentMethodId = p.PaymentMethodId,
                PaymentMethodName = p.PaymentMethod != null ? p.PaymentMethod.Name : "Desconocido",
                Amount = p.Amount,
                AmountBsS = p.AmountBsS,
                ExchangeRate = p.ExchangeRate,
                ReferenceNumber = p.ReferenceNumber,
                CreatedAt = p.CreatedAt
            }).ToList()
        };
    }

    /// <summary>
    /// Ancla la tasa de cambio recibida del cliente a la tasa BCV del día (8.5-A5/8.6-B3).
    /// - Desvío ≤ tolerancia configurable (default 10%): se acepta la tasa recibida.
    /// - Desvío > tolerancia: se ANCLA la tasa BCV del día como tasa efectiva (audit).
    /// - Desvío ≥ ±100%: rechazo por posible manipulación.
    /// - Sin BCV del día disponible (0/no registrado): se continúa con la tasa recibida (fail-open auditable,
    ///   sin catch-swallow: los fallos del servicio BCV se registran y NO se ignoran silenciosamente).
    /// </summary>
    private async Task<decimal> ResolveAnchoredRateAsync(decimal clientRate, string contextLabel, int referenceId)
    {
        if (clientRate <= 0m)
        {
            throw new InvalidOperationException("Rechazo Defensivo: Tasa de cambio AppliedRate inválida o no inicializada (<= 0).");
        }

        if (_inventoryService == null)
        {
            _logger?.LogWarning("[A5-AUDIT] Servicio de inventario/BCV no disponible en {Context} #{Ref}. Se usa la tasa recibida: {Rate}", contextLabel, referenceId, clientRate);
            return clientRate;
        }

        decimal officialRate = 0m;
        try
        {
            officialRate = await _inventoryService.GetTodayExchangeRateAsync();
        }
        catch (System.Exception ex)
        {
            // 8.5-A5: SIN catch-swallow. Se audita el fallo y se continúa con fail-open ordenado.
            _logger?.LogError(ex, "[A5-AUDIT] Error obteniendo la tasa BCV del día en {Context} #{Ref}. Fail-open: se usa la tasa recibida {Rate}.", contextLabel, referenceId, clientRate);
            return clientRate;
        }

        if (officialRate <= 0m)
        {
            _logger?.LogWarning("[A5-AUDIT] Sin tasa BCV del día registrada en {Context} #{Ref}. Fail-open: se usa la tasa recibida {Rate}.", contextLabel, referenceId, clientRate);
            return clientRate;
        }

        decimal deviationPct = Math.Abs(clientRate - officialRate) / officialRate;

        if (deviationPct >= 1.0m)
        {
            _logger?.LogError("[A5-AUDIT] Tasa rechazada por posible manipulación. {Context} #{Ref}, TasaRecibida={Received}, TasaBCV={Official}, Desvío={Deviation:P2}", contextLabel, referenceId, clientRate, officialRate, deviationPct);
            throw new InvalidOperationException($"La tasa de cambio {clientRate} fue rechazada: excede ±100% de la tasa BCV oficial ({officialRate}). Contacte al supervisor.");
        }

        decimal tolerancePct = 0.10m;
        if (_settingsService != null)
        {
            try
            {
                var toleranceSetting = await _settingsService.GetSettingAsync("RateDeviationTolerancePct");
                if (decimal.TryParse(toleranceSetting, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                    && parsed > 0m && parsed < 1.0m)
                {
                    tolerancePct = parsed;
                }
            }
            catch (System.Exception ex)
            {
                _logger?.LogWarning(ex, "[A5-AUDIT] No se pudo leer la tolerancia configurada en {Context} #{Ref}; se usa el default {Tolerance:P2}.", contextLabel, referenceId, tolerancePct);
            }
        }

        if (deviationPct > tolerancePct)
        {
            _logger?.LogWarning("[A5-AUDIT] Desvío de tasa significativo ({Deviation:P2} > {Tolerance:P2}) en {Context} #{Ref}. Se ANCLA a la tasa BCV del día: {Received} -> {Official}", deviationPct, tolerancePct, contextLabel, referenceId, clientRate, officialRate);
            return officialRate;
        }

        return clientRate;
    }
}
