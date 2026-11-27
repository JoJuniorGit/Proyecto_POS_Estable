using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Entities;

namespace Sales.Module.Services;

/// <summary>
/// CxC (historial realizado): deudas pagadas, cronologia por obligacion y bitacora de abonos.
/// Todos los montos se leen de los snapshots persistidos del cobro (tasa y moneda historicas);
/// nunca se reproyectan a la tasa del dia.
/// </summary>
public partial class CreditService
{
    /// <inheritdoc />
    public async Task<PaidReceivablesListDto> GetPaidReceivablesAsync(
        PaidReceivablesFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        filter ??= new PaidReceivablesFilterDto();
        int page = Math.Max(1, filter.Page ?? 1);
        int pageSize = Math.Clamp(filter.PageSize ?? 25, 1, 200);
        var (fromUtc, toExclusiveUtc) = TimeZoneHelper.GetUtcRange(
            filter.PaidFrom,
            filter.PaidTo,
            defaultEndDateToToday: false);

        var paidReceivableIds = await _context.Receivables
            .AsNoTracking()
            .Where(receivable => receivable.Status == ReceivableStatus.Pagada
                && receivable.PaymentAllocations.Any(allocation => !allocation.ReceivablePayment.IsVoided))
            .Select(receivable => receivable.Id)
            .ToListAsync(cancellationToken);

        if (paidReceivableIds.Count == 0)
        {
            return new PaidReceivablesListDto { Page = page, PageSize = pageSize };
        }

        var receivables = await _context.Receivables
            .AsNoTracking()
            .Include(receivable => receivable.Customer)
            .Include(receivable => receivable.Sale)
            .Where(receivable => paidReceivableIds.Contains(receivable.Id))
            .ToListAsync(cancellationToken);

        var allocations = await _context.CreditPaymentAllocations
            .AsNoTracking()
            .AsSplitQuery()
            .Where(allocation => paidReceivableIds.Contains(allocation.ReceivableId))
            .Include(allocation => allocation.ReceivablePayment).ThenInclude(payment => payment.PaymentMethod)
            .Include(allocation => allocation.ReceivablePayment).ThenInclude(payment => payment.ReceivedByUser)
            .ToListAsync(cancellationToken);

        var allocationsByReceivable = allocations
            .GroupBy(allocation => allocation.ReceivableId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var rows = new List<(PaidReceivableItemDto Item, List<CreditPaymentAllocation> Allocations)>();
        foreach (var receivable in receivables)
        {
            var receivableAllocations = allocationsByReceivable.GetValueOrDefault(receivable.Id) ?? new List<CreditPaymentAllocation>();
            rows.Add((BuildPaidReceivableItem(receivable, receivableAllocations), receivableAllocations));
        }

        IEnumerable<(PaidReceivableItemDto Item, List<CreditPaymentAllocation> Allocations)> filtered = rows;

        if (fromUtc.HasValue)
        {
            filtered = filtered.Where(row => row.Item.PaidAt >= fromUtc.Value);
        }

        if (toExclusiveUtc.HasValue)
        {
            filtered = filtered.Where(row => row.Item.PaidAt < toExclusiveUtc.Value);
        }

        if (filter.PaymentMethodId.HasValue)
        {
            int methodId = filter.PaymentMethodId.Value;
            filtered = filtered.Where(row =>
                row.Allocations.Any(allocation => allocation.ReceivablePayment.PaymentMethodId == methodId));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            string term = filter.Search.Trim();
            filtered = filtered.Where(row =>
                MatchesPaidSearch(row.Item, row.Allocations, term));
        }

        var ordered = filtered
            .OrderByDescending(row => row.Item.PaidAt)
            .ThenByDescending(row => row.Item.ObligationId)
            .ToList();

        var summary = BuildPaidSummary(ordered);
        int totalCount = ordered.Count;
        var pageItems = ordered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(row => row.Item)
            .ToList();

        return new PaidReceivablesListDto
        {
            Items = pageItems,
            Summary = summary,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            HasMore = page * pageSize < totalCount
        };
    }

    /// <inheritdoc />
    public async Task<ReceivablePaymentTimelineDto> GetReceivablePaymentTimelineAsync(
        int obligationId,
        CancellationToken cancellationToken = default)
    {
        var receivable = await _context.Receivables
            .AsNoTracking()
            .Include(entity => entity.Customer)
            .Include(entity => entity.Sale)
            .FirstOrDefaultAsync(entity => entity.Id == obligationId, cancellationToken)
            ?? throw new KeyNotFoundException($"Obligación CxC {obligationId} no encontrada.");

        var allocations = await _context.CreditPaymentAllocations
            .AsNoTracking()
            .AsSplitQuery()
            .Where(allocation => allocation.ReceivableId == obligationId)
            .Include(allocation => allocation.ReceivablePayment).ThenInclude(payment => payment.PaymentMethod)
            .Include(allocation => allocation.ReceivablePayment).ThenInclude(payment => payment.ReceivedByUser)
            .OrderBy(allocation => allocation.ReceivablePayment.ReceivedAtUtc)
            .ThenBy(allocation => allocation.ReceivablePaymentId)
            .ThenBy(allocation => allocation.Id)
            .ToListAsync(cancellationToken);

        var paymentAllocationCounts = await _context.CreditPaymentAllocations
            .AsNoTracking()
            .Where(allocation => allocation.ReceivableId == obligationId)
            .GroupBy(allocation => allocation.ReceivablePaymentId)
            .Select(group => new { PaymentId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(entry => entry.PaymentId, entry => entry.Count, cancellationToken);

        decimal runningBalance = Math.Round(receivable.OriginalUSD, 4, MidpointRounding.AwayFromZero);
        var timeline = new List<ObligationPaymentTimelineItemDto>(allocations.Count);
        int sequence = 1;

        foreach (var allocation in allocations)
        {
            var payment = allocation.ReceivablePayment;
            string currency = PaymentMethodCurrencyResolver.ResolveStoredOrName(
                payment.PaymentMethod?.Currency,
                payment.PaymentMethod?.Name);
            decimal amountPaidInCurrency = string.Equals(currency, PaymentMethodCurrencyResolver.Usd, StringComparison.Ordinal)
                ? allocation.AmountUSD
                : allocation.AmountBsS;

            if (!payment.IsVoided)
            {
                runningBalance = Math.Max(
                    0m,
                    Math.Round(runningBalance - allocation.AmountUSD, 4, MidpointRounding.AwayFromZero));
            }


            timeline.Add(new ObligationPaymentTimelineItemDto
            {
                PaymentId = payment.Id,
                SequenceNumber = sequence++,
                PaidAt = payment.ReceivedAtUtc,
                PaymentMethodName = payment.PaymentMethod?.Name ?? "Método no disponible",
                PaymentCurrency = currency,
                AmountPaidInCurrency = amountPaidInCurrency,
                ExchangeRateApplied = payment.PaymentRate,
                AmountCreditedUsd = allocation.AmountUSD,
                Reference = payment.ReferenceNumber,
                ImputationMode = paymentAllocationCounts.GetValueOrDefault(payment.Id) > 1 ? "FIFO" : "Factura específica",
                CashierName = payment.ReceivedByUser?.Name ?? "Usuario",
                RunningBalanceAfterUsd = runningBalance,
                Status = payment.IsVoided ? "Anulado" : "Aplicado",
                Notes = payment.VoidReason
            });
        }

        decimal totalPaidUsd = allocations
            .Where(allocation => !allocation.ReceivablePayment.IsVoided)
            .Sum(allocation => allocation.AmountUSD);

        return new ReceivablePaymentTimelineDto
        {
            ObligationId = receivable.Id,
            InvoiceNumber = receivable.Sale?.InvoiceNumber,
            SaleId = receivable.SaleId,
            CustomerName = receivable.Customer?.Name ?? "Cliente",
            CustomerDocument = receivable.Customer?.CedulaOrRif ?? string.Empty,
            Status = receivable.Status.ToString(),
            OriginalAmountUsd = receivable.OriginalUSD,
            TotalPaidUsd = totalPaidUsd,
            BalanceUsd = receivable.BalanceUSD,
            IssuedAt = receivable.IssuedAtUtc,
            DueDate = receivable.DueDateUtc,
            Payments = timeline
        };
    }

    /// <inheritdoc />
    public async Task<PaidReceivablePaymentsListDto> GetPaidReceivablePaymentsAsync(
        PaidReceivablesFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        filter ??= new PaidReceivablesFilterDto();
        int page = Math.Max(1, filter.Page ?? 1);
        int pageSize = Math.Clamp(filter.PageSize ?? 25, 1, 200);
        var (fromUtc, toExclusiveUtc) = TimeZoneHelper.GetUtcRange(
            filter.PaidFrom,
            filter.PaidTo,
            defaultEndDateToToday: false);

        var paymentsQuery = _context.ReceivablePayments
            .AsNoTracking()
            .AsSplitQuery()
            .Where(payment => payment.Allocations.Any(allocation =>
                allocation.Receivable.Status == ReceivableStatus.Pagada));

        if (fromUtc.HasValue)
        {
            paymentsQuery = paymentsQuery.Where(payment => payment.ReceivedAtUtc >= fromUtc.Value);
        }

        if (toExclusiveUtc.HasValue)
        {
            paymentsQuery = paymentsQuery.Where(payment => payment.ReceivedAtUtc < toExclusiveUtc.Value);
        }

        if (filter.PaymentMethodId.HasValue)
        {
            int methodId = filter.PaymentMethodId.Value;
            paymentsQuery = paymentsQuery.Where(payment => payment.PaymentMethodId == methodId);
        }

        var payments = await paymentsQuery
            .Include(payment => payment.Customer)
            .Include(payment => payment.PaymentMethod)
            .Include(payment => payment.ReceivedByUser)
            .Include(payment => payment.Allocations).ThenInclude(allocation => allocation.Receivable).ThenInclude(receivable => receivable.Sale)
            .OrderByDescending(payment => payment.ReceivedAtUtc)
            .ThenByDescending(payment => payment.Id)
            .ToListAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            string term = filter.Search.Trim();
            payments = payments.Where(payment => MatchesPaymentSearch(payment, term)).ToList();
        }

        var summarySource = await GetPaidReceivablesAsync(new PaidReceivablesFilterDto
        {
            Search = filter.Search,
            PaidFrom = filter.PaidFrom,
            PaidTo = filter.PaidTo,
            PaymentMethodId = filter.PaymentMethodId
        }, cancellationToken);

        int totalCount = payments.Count;
        var items = payments
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(BuildPaidPaymentItem)
            .ToList();

        return new PaidReceivablePaymentsListDto
        {
            Items = items,
            Summary = summarySource.Summary,
            TotalCount = totalCount,
            Page = page,
            PageSize = pageSize,
            HasMore = page * pageSize < totalCount
        };
    }

    private static PaidReceivableItemDto BuildPaidReceivableItem(
        Receivable receivable,
        List<CreditPaymentAllocation> allocations)
    {
        var ordered = allocations
            .Where(allocation => !allocation.ReceivablePayment.IsVoided)
            .OrderBy(allocation => allocation.ReceivablePayment.ReceivedAtUtc)
            .ThenBy(allocation => allocation.ReceivablePaymentId)
            .ThenBy(allocation => allocation.Id)
            .ToList();

        DateTime paidAt = ordered.Count > 0
            ? ordered[^1].ReceivablePayment.ReceivedAtUtc
            : receivable.IssuedAtUtc;
        decimal totalPaidUsd = ordered.Count > 0
            ? ordered.Sum(allocation => allocation.AmountUSD)
            : receivable.OriginalUSD;
        decimal totalPaidLocal = ordered.Count > 0
            ? ordered.Sum(allocation => allocation.AmountBsS)
            : receivable.OriginalBsS;

        var methods = ordered
            .Select(allocation => allocation.ReceivablePayment.PaymentMethod?.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        int daysToSettle = Math.Max(0, (paidAt.Date - receivable.IssuedAtUtc.Date).Days);

        return new PaidReceivableItemDto
        {
            ObligationId = receivable.Id,
            InvoiceNumber = receivable.Sale?.InvoiceNumber,
            SaleId = receivable.SaleId,
            CustomerName = receivable.Customer?.Name ?? "Cliente",
            CustomerDocument = receivable.Customer?.CedulaOrRif ?? string.Empty,
            IssuedAt = receivable.IssuedAtUtc,
            DueDate = receivable.DueDateUtc,
            PaidAt = paidAt,
            DaysToSettle = daysToSettle,
            SettledOnTime = paidAt <= receivable.DueDateUtc,
            OriginalAmountUsd = receivable.OriginalUSD,
            TotalPaidUsd = totalPaidUsd,
            TotalPaidLocalHistorical = totalPaidLocal,
            PaymentsCount = ordered.Select(allocation => allocation.ReceivablePaymentId).Distinct().Count(),
            PaymentMethodsSummary = string.Join(" · ", methods)
        };
    }

    private static PaidReceivablesSummaryDto BuildPaidSummary(
        List<(PaidReceivableItemDto Item, List<CreditPaymentAllocation> Allocations)> rows)
    {
        if (rows.Count == 0)
        {
            return new PaidReceivablesSummaryDto();
        }

        var paymentIds = rows
            .SelectMany(row => row.Allocations
                .Where(allocation => !allocation.ReceivablePayment.IsVoided)
                .Select(allocation => allocation.ReceivablePaymentId))
            .Distinct()
            .ToList();

        return new PaidReceivablesSummaryDto
        {
            PaidDebtsCount = rows.Count,
            TotalCollectedUsd = rows.Sum(row => row.Item.TotalPaidUsd),
            TotalCollectedLocalHistorical = rows.Sum(row => row.Item.TotalPaidLocalHistorical),
            TotalPaymentsCount = paymentIds.Count,
            AverageDaysToSettle = Math.Round(rows.Average(row => (decimal)row.Item.DaysToSettle), 1, MidpointRounding.AwayFromZero)
        };
    }

    private static bool MatchesPaidSearch(
        PaidReceivableItemDto item,
        List<CreditPaymentAllocation> allocations,
        string term)
    {
        if (item.CustomerName.Contains(term, StringComparison.OrdinalIgnoreCase)
            || item.CustomerDocument.Contains(term, StringComparison.OrdinalIgnoreCase)
            || (item.InvoiceNumber?.ToString().Contains(term, StringComparison.OrdinalIgnoreCase) ?? false)
            || item.PaymentMethodsSummary.Contains(term, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return allocations.Any(allocation =>
            allocation.ReceivablePayment.ReferenceNumber?.Contains(term, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static bool MatchesPaymentSearch(ReceivablePayment payment, string term)
    {
        if (payment.Customer?.Name.Contains(term, StringComparison.OrdinalIgnoreCase) == true
            || payment.Customer?.CedulaOrRif.Contains(term, StringComparison.OrdinalIgnoreCase) == true
            || payment.ReferenceNumber?.Contains(term, StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        return payment.Allocations.Any(allocation =>
            allocation.Receivable?.Sale?.InvoiceNumber?.ToString().Contains(term, StringComparison.OrdinalIgnoreCase) == true);
    }

    private static PaidReceivablePaymentItemDto BuildPaidPaymentItem(ReceivablePayment payment)
    {
        string currency = PaymentMethodCurrencyResolver.ResolveStoredOrName(
            payment.PaymentMethod?.Currency,
            payment.PaymentMethod?.Name);
        decimal amountPaidInCurrency = string.Equals(currency, PaymentMethodCurrencyResolver.Usd, StringComparison.Ordinal)
            ? payment.AmountUSD
            : payment.AmountBsS;

        var allocations = payment.Allocations
            .OrderBy(allocation => allocation.ReceivableId)
            .Select(allocation => new PaidPaymentAllocationDto
            {
                ObligationId = allocation.ReceivableId,
                InvoiceNumber = allocation.Receivable?.Sale?.InvoiceNumber,
                AmountUsd = allocation.AmountUSD,
                AmountBsS = allocation.AmountBsS
            })
            .ToList();

        string invoiceSummary = BuildInvoiceSummary(allocations);

        return new PaidReceivablePaymentItemDto
        {
            PaymentId = payment.Id,
            PaidAt = payment.ReceivedAtUtc,
            CashierName = payment.ReceivedByUser?.Name ?? "Usuario",
            CustomerName = payment.Customer?.Name ?? "Cliente",
            CustomerDocument = payment.Customer?.CedulaOrRif ?? string.Empty,
            PaymentMethodName = payment.PaymentMethod?.Name ?? "Método no disponible",
            PaymentCurrency = currency,
            AmountPaidInCurrency = amountPaidInCurrency,
            ExchangeRateApplied = payment.PaymentRate,
            AmountCreditedUsd = payment.AmountUSD,
            Reference = payment.ReferenceNumber,
            ImputationMode = allocations.Count > 1 ? "FIFO" : "Factura específica",
            Status = payment.IsVoided ? "Anulado" : "Aplicado",
            Allocations = allocations,
            InvoiceSummary = invoiceSummary
        };
    }

    private static string BuildInvoiceSummary(List<PaidPaymentAllocationDto> allocations)
    {
        var invoices = allocations
            .Select(allocation => allocation.InvoiceNumber.HasValue
                ? $"F{allocation.InvoiceNumber.Value:D5}"
                : $"CxC-{allocation.ObligationId}")
            .Distinct()
            .ToList();

        if (invoices.Count == 0)
        {
            return "—";
        }

        return invoices.Count == 1
            ? invoices[0]
            : $"{invoices[0]} + {invoices.Count - 1} más";
    }
}

