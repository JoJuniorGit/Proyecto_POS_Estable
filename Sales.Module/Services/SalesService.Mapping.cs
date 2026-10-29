using Core.DTOs;
using Core.Entities;
using Core.Events;
using Core.Helpers;
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

    public async Task<(string Status, int? CashierId)?> GetSaleAuthMetadataAsync(int saleId, System.Threading.CancellationToken cancellationToken = default)
    {
        var meta = await _context.Sales
            .AsNoTracking()
            .Where(s => s.Id == saleId)
            .Select(s => new { s.Status, s.CashierId })
            .FirstOrDefaultAsync(cancellationToken);

        return meta is null ? null : (meta.Status.ToString(), meta.CashierId);
    }

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
            EffectiveTotalUSD = sale.EffectiveTotalUSD,
            AppliedRate = sale.AppliedRate,
            TotalBsS = sale.TotalBsS,
            FinalPaidAmountBsS = sale.FinalPaidAmountBsS,
            SubtotalBsS = sale.SubtotalBsS,
            PrimaryCurrencyCode = sale.PrimaryCurrencyCode,
            SecondaryCurrencyCode = sale.SecondaryCurrencyCode,
            MonetaryMode = sale.MonetaryMode,
            PrimaryAmount = sale.PrimaryAmount,
            SecondaryAmount = sale.SecondaryAmount,
            AppliedRateSource = sale.AppliedRateSource,
            CashierId = sale.CashierId,
            CashierName = sale.Cashier != null ? (string.IsNullOrWhiteSpace(sale.Cashier.Name) ? sale.Cashier.FullName : sale.Cashier.Name) : "Usuario Desconocido",
            CustomerName = sale.CustomerName,
            CustomerCedula = sale.CustomerCedula,
            DeliveryStatus = sale.DeliveryStatus.ToString(),
            PickupDate = sale.PickupDate,
            PriceListType = string.IsNullOrWhiteSpace(sale.PriceListType) ? "Retail" : sale.PriceListType,
            ClaimedByUserId = sale.ClaimedByUserId,
            ClaimedByUserName = sale.ClaimedByUserName,
            ClaimAction = sale.ClaimAction.ToString(),
            ClaimedAtUtc = sale.ClaimedAtUtc,
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
                IsCustomPrice = i.IsCustomPrice,
                RoundingRuleCashBs = i.RoundingRuleCashBs
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
                CreatedAt = p.CreatedAt,
                CurrencyCode = p.CurrencyCode,
                OriginalAmount = p.OriginalAmount
            }).ToList()
        };
    }

    /// <summary>
    /// 8.152 (SEC-08): resuelve la tasa efectiva del checkout-preview delegando en el resolver
    /// compartido con contexto "CheckoutPreview" (misma semántica del completar venta). La
    /// cancelación se observa al entrar; el resolver privado no recibe token (limitación
    /// preexistente documentada).
    /// </summary>
    public async Task<decimal> ResolveCheckoutRateAsync(int saleId, decimal clientRate, System.Threading.CancellationToken cancellationToken = default)
    {
        // La cancelación se observa al entrar; el resolver privado no recibe token
        // (limitación preexistente compartida con los demás flujos que lo invocan).
        cancellationToken.ThrowIfCancellationRequested();
        return await ResolveAnchoredRateAsync(clientRate, contextLabel: "CheckoutPreview", referenceId: saleId);
    }

    /// <summary>
    /// 8.205 (C-01): la política vive en ExchangeRateAnchorService; este método conserva el
    /// mensaje público de rechazo que consumen checkout y los tests de integridad financiera.
    /// </summary>
    private async Task<decimal> ResolveAnchoredRateAsync(decimal clientRate, string contextLabel, int referenceId)
    {
        var result = await _rateAnchor.ResolveAsync(clientRate, contextLabel, referenceId);
        if (result.Outcome == ExchangeRateAnchorOutcome.Rejected)
        {
            // 8.211-T3c3c1 (S4): the rejection names the official source that was actually used.
            string officialLabel = result.RateSource is CurrencyRateSource.Manual or CurrencyRateSource.ManualInverse
                ? "manual del par"
                : "BCV oficial";
            throw new ArgumentException($"La tasa de cambio {result.ClientRate} fue rechazada: excede ±100% de la tasa {officialLabel} ({result.OfficialRate}). Contacte al supervisor.");
        }

        return result.EffectiveRate;
    }
}
