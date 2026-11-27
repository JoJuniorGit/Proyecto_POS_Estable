using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Common;
using Core.Constants;
using Core.DTOs;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;

namespace Sales.Module.Services;

/// <summary>
/// 8.211-T3c3b3 (S7/S11): after saving a Single 2dp profile on an install without financial data,
/// make the catalog usable in the primary currency. The canonical seed methods are reused in
/// place; legacy USD/local duplicates are deactivated only while unused. A method referenced by
/// any financial record is never rewritten: a suffixed copy is created instead.
/// </summary>
public class MonetaryPaymentMethodSeeder : IMonetaryPaymentMethodSeeder
{
    private sealed record CanonicalMethod(string Name, bool IsCash, bool RequiresReference, int DisplayOrder);

    private static readonly CanonicalMethod[] CanonicalMethods =
    {
        new("Efectivo", IsCash: true, RequiresReference: false, DisplayOrder: 1),
        new("Tarjeta (Punto de Venta)", IsCash: false, RequiresReference: true, DisplayOrder: 2),
        new("Transferencia / Pago Móvil", IsCash: false, RequiresReference: true, DisplayOrder: 3)
    };

    private static readonly string[] LegacySeedNames = { "Cash", "Card", "Divisas (USD)" };

    private readonly SalesDbContext _context;
    private readonly IMemoryCache? _cache;
    private readonly IPaymentMethodNotifier? _notifier;

    public MonetaryPaymentMethodSeeder(
        SalesDbContext context,
        IMemoryCache? cache = null,
        IPaymentMethodNotifier? notifier = null)
    {
        ArgumentNullException.ThrowIfNull(context);

        _context = context;
        _cache = cache;
        _notifier = notifier;
    }

    public async Task EnsureForProfileAsync(
        MonetaryProfileDto profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();

        if (profile.Mode != MonetaryMode.Single)
        {
            return;
        }

        var primaryCurrency = CurrencyCodeNormalizer.NormalizeToIso(profile.PrimaryCurrencyCode);
        if (primaryCurrency is null)
        {
            return;
        }

        // Only the supported Single currencies (digital and cash both 2dp) are seeded. VES is
        // excluded because its historical cash policy rounds to whole units.
        var precision = CurrencyPrecisionCatalog.For(primaryCurrency);
        if (precision.DigitalDecimals != 2 || precision.CashDecimals != 2)
        {
            return;
        }

        var methods = await _context.PaymentMethods
            .Where(method => !method.IsDeleted)
            .ToListAsync(cancellationToken);

        var usedMethodIds = await LoadUsedMethodIdsAsync(cancellationToken);
        var takenOrders = methods.Select(method => method.DisplayOrder).ToHashSet();
        var nextDisplayOrder = methods.Count == 0 ? 1 : methods.Max(method => method.DisplayOrder) + 1;
        if (nextDisplayOrder < 1)
        {
            nextDisplayOrder = 1;
        }

        var hasChanges = false;

        foreach (var canonical in CanonicalMethods)
        {
            hasChanges |= EnsureCanonicalMethod(
                canonical,
                primaryCurrency,
                methods,
                usedMethodIds,
                takenOrders,
                ref nextDisplayOrder);
        }

        foreach (var legacyName in LegacySeedNames)
        {
            foreach (var legacy in methods.Where(method =>
                !usedMethodIds.Contains(method.Id) && NameMatches(method.Name, legacyName)))
            {
                if (legacy.IsActive)
                {
                    legacy.IsActive = false;
                    hasChanges = true;
                }
            }
        }

        if (!hasChanges)
        {
            return;
        }

        await _context.SaveChangesAsync(cancellationToken);

        _cache?.Remove(CacheKeys.ActivePaymentMethods);
        _cache?.Remove(CacheKeys.AllPaymentMethods);

        if (_notifier is not null)
        {
            await _notifier.NotifyPaymentMethodsUpdatedAsync();
        }
    }

    private bool EnsureCanonicalMethod(
        CanonicalMethod canonical,
        string primaryCurrency,
        List<PaymentMethod> methods,
        ISet<int> usedMethodIds,
        ISet<int> takenOrders,
        ref int nextDisplayOrder)
    {
        var exactMatches = methods
            .Where(method => NameMatches(method.Name, canonical.Name))
            .ToList();

        var reusable = exactMatches.FirstOrDefault(method => !usedMethodIds.Contains(method.Id));
        if (reusable is not null)
        {
            takenOrders.Add(canonical.DisplayOrder);
            return ApplyCanonicalConfiguration(
                reusable,
                canonical,
                primaryCurrency,
                CanApplyDisplayOrder(reusable, canonical.DisplayOrder, methods));
        }

        if (exactMatches.Count == 0)
        {
            return AddCanonicalMethod(
                canonical,
                canonical.Name,
                primaryCurrency,
                methods,
                takenOrders,
                ref nextDisplayOrder);
        }

        // The canonical name belongs to financial history: it must keep its currency and flags.
        // The suffixed alias produced by a previous run is reused when it is still unused.
        var suffixedName = $"{canonical.Name} {primaryCurrency}";
        var suffixedMatches = methods
            .Where(method => NameMatches(method.Name, suffixedName))
            .ToList();

        var reusableSuffixed = suffixedMatches.FirstOrDefault(method => !usedMethodIds.Contains(method.Id));
        if (reusableSuffixed is not null)
        {
            // The canonical order is normally held by the used method, so the alias keeps the
            // free slot it was assigned the first time and a second run is a no-op.
            return ApplyCanonicalConfiguration(
                reusableSuffixed,
                canonical,
                primaryCurrency,
                CanApplyDisplayOrder(reusableSuffixed, canonical.DisplayOrder, methods));
        }

        if (suffixedMatches.Count > 0)
        {
            // Both candidate names are already referenced by financial data. Creating another
            // variant would only duplicate the catalog without a safe currency to claim.
            return false;
        }

        return AddCanonicalMethod(
            canonical,
            suffixedName,
            primaryCurrency,
            methods,
            takenOrders,
            ref nextDisplayOrder);
    }

    private bool AddCanonicalMethod(
        CanonicalMethod canonical,
        string name,
        string primaryCurrency,
        List<PaymentMethod> methods,
        ISet<int> takenOrders,
        ref int nextDisplayOrder)
    {
        var displayOrder = canonical.DisplayOrder;
        if (takenOrders.Contains(displayOrder))
        {
            while (takenOrders.Contains(nextDisplayOrder))
            {
                nextDisplayOrder++;
            }

            displayOrder = nextDisplayOrder;
            nextDisplayOrder++;
        }

        takenOrders.Add(displayOrder);

        var method = new PaymentMethod
        {
            Name = name,
            IsActive = true,
            IsCash = canonical.IsCash,
            RequiresReference = canonical.RequiresReference,
            DisplayOrder = displayOrder,
            Currency = primaryCurrency,
            IsDeleted = false
        };

        _context.PaymentMethods.Add(method);
        methods.Add(method);
        return true;
    }

    // A reused method keeps its current order when the canonical slot is occupied by another
    // catalog row; otherwise the second run would reorder the catalog (or duplicate an order).
    private static bool CanApplyDisplayOrder(
        PaymentMethod method,
        int displayOrder,
        List<PaymentMethod> methods)
        => method.DisplayOrder == displayOrder
            || !methods.Any(other =>
                !ReferenceEquals(other, method) && other.DisplayOrder == displayOrder);

    private static bool ApplyCanonicalConfiguration(
        PaymentMethod method,
        CanonicalMethod canonical,
        string primaryCurrency,
        bool applyDisplayOrder)
    {
        var changed = false;

        if (!string.Equals(method.Currency, primaryCurrency, StringComparison.Ordinal))
        {
            method.Currency = primaryCurrency;
            changed = true;
        }

        if (!method.IsActive)
        {
            method.IsActive = true;
            changed = true;
        }

        if (method.IsCash != canonical.IsCash)
        {
            method.IsCash = canonical.IsCash;
            changed = true;
        }

        if (method.RequiresReference != canonical.RequiresReference)
        {
            method.RequiresReference = canonical.RequiresReference;
            changed = true;
        }

        if (applyDisplayOrder && method.DisplayOrder != canonical.DisplayOrder)
        {
            method.DisplayOrder = canonical.DisplayOrder;
            changed = true;
        }

        return changed;
    }

    private async Task<HashSet<int>> LoadUsedMethodIdsAsync(CancellationToken cancellationToken)
    {
        var usedMethodIds = new HashSet<int>();

        usedMethodIds.UnionWith(await _context.SalePayments
            .AsNoTracking()
            .Select(payment => payment.PaymentMethodId)
            .Distinct()
            .ToListAsync(cancellationToken));

        usedMethodIds.UnionWith(await _context.CashTransactions
            .AsNoTracking()
            .Where(transaction => transaction.PaymentMethodId != null)
            .Select(transaction => transaction.PaymentMethodId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken));

        usedMethodIds.UnionWith(await _context.ClosureDetails
            .AsNoTracking()
            .Select(detail => detail.PaymentMethodId)
            .Distinct()
            .ToListAsync(cancellationToken));

        usedMethodIds.UnionWith(await _context.ReceivablePayments
            .AsNoTracking()
            .Where(payment => payment.PaymentMethodId != null)
            .Select(payment => payment.PaymentMethodId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken));

        return usedMethodIds;
    }

    private static bool NameMatches(string? candidate, string expected)
        => candidate is not null
            && string.Equals(candidate.Trim(), expected, StringComparison.OrdinalIgnoreCase);
}
