using Core.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Sales.Module.Services;

public class PaymentMethodService : IPaymentMethodService
{
    private readonly SalesDbContext _context;
    private readonly IMemoryCache? _cache;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public PaymentMethodService(SalesDbContext context, IMemoryCache? cache = null)
    {
        _context = context;
        _cache = cache;
    }

    public async Task<IEnumerable<PaymentMethod>> GetActiveMethodsAsync()
    {
        if (_cache != null && _cache.TryGetValue(CacheKeys.ActivePaymentMethods, out IEnumerable<PaymentMethod>? cached) && cached != null)
        {
            return cached;
        }

        var methods = await _context.PaymentMethods
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .ToListAsync();

        var result = methods ?? (IEnumerable<PaymentMethod>)Enumerable.Empty<PaymentMethod>();

        _cache?.Set(CacheKeys.ActivePaymentMethods, result, CacheDuration);
        return result;
    }

    public async Task<IEnumerable<PaymentMethod>> GetAllAsync()
    {
        if (_cache != null && _cache.TryGetValue(CacheKeys.AllPaymentMethods, out IEnumerable<PaymentMethod>? cached) && cached != null)
        {
            return cached;
        }

        var methods = await _context.PaymentMethods
            .AsNoTracking()
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .ToListAsync();

        var result = methods ?? (IEnumerable<PaymentMethod>)Enumerable.Empty<PaymentMethod>();

        _cache?.Set(CacheKeys.AllPaymentMethods, result, CacheDuration);
        return result;
    }

    public async Task<PaymentMethod> GetByIdAsync(int id)
    {
        var method = await _context.PaymentMethods.FindAsync(id);
        if (method == null) throw new KeyNotFoundException($"Payment method {id} not found.");
        return method;
    }

    public async Task<PaymentMethod> CreateAsync(PaymentMethod method)
    {
        _context.PaymentMethods.Add(method);
        await _context.SaveChangesAsync();
        InvalidateCache();
        return method;
    }

    public async Task<PaymentMethod> UpdateAsync(PaymentMethod method)
    {
        var existing = await _context.PaymentMethods.FindAsync(method.Id);
        if (existing == null) throw new KeyNotFoundException($"Payment method {method.Id} not found.");

        existing.Name = method.Name;
        existing.IsActive = method.IsActive;
        existing.RequiresReference = method.RequiresReference;
        existing.DisplayOrder = method.DisplayOrder;
        existing.IsCash = method.IsCash;

        await _context.SaveChangesAsync();
        InvalidateCache();
        return existing;
    }

    public async Task DeleteAsync(int id)
    {
        // Implement logical deletion as requested to preserve historical referential integrity
        var existing = await _context.PaymentMethods.FindAsync(id);
        if (existing != null)
        {
            existing.IsActive = false;
            await _context.SaveChangesAsync();
            InvalidateCache();
        }
    }

    private void InvalidateCache()
    {
        _cache?.Remove(CacheKeys.ActivePaymentMethods);
        _cache?.Remove(CacheKeys.AllPaymentMethods);
    }
}
