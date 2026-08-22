using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Sales.Module.Services;

public class PaymentMethodService : IPaymentMethodService
{
    private readonly SalesDbContext _context;

    public PaymentMethodService(SalesDbContext context)
    {
        _context = context;
    }

    public async Task<IEnumerable<PaymentMethod>> GetActiveMethodsAsync()
    {
        var methods = await _context.PaymentMethods
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .ToListAsync();

        return methods ?? Enumerable.Empty<PaymentMethod>();
    }

    public async Task<IEnumerable<PaymentMethod>> GetAllAsync()
    {
        var methods = await _context.PaymentMethods
            .AsNoTracking()
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .ToListAsync();

        return methods ?? Enumerable.Empty<PaymentMethod>();
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
        }
    }
}
