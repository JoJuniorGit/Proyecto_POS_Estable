using Core.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
    private readonly IPaymentMethodNotifier? _notifier;
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    public PaymentMethodService(
        SalesDbContext context, 
        IMemoryCache? cache = null,
        IPaymentMethodNotifier? notifier = null)
    {
        _context = context;
        _cache = cache;
        _notifier = notifier;
    }

    public async Task<IEnumerable<PaymentMethod>> GetActiveMethodsAsync()
    {
        try
        {
            if (_cache != null && _cache.TryGetValue(CacheKeys.ActivePaymentMethods, out IEnumerable<PaymentMethod>? cached) && cached != null)
            {
                return cached;
            }
        }
        catch { }

        var methods = await _context.PaymentMethods
            .AsNoTracking()
            .Where(p => !p.IsDeleted && p.IsActive)
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .ToListAsync();

        var result = methods ?? (IEnumerable<PaymentMethod>)Enumerable.Empty<PaymentMethod>();

        try
        {
            _cache?.Set(CacheKeys.ActivePaymentMethods, result, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CacheDuration,
                Size = 1
            });
        }
        catch { }

        return result;
    }

    public async Task<IEnumerable<PaymentMethod>> GetAllAsync()
    {
        try
        {
            if (_cache != null && _cache.TryGetValue(CacheKeys.AllPaymentMethods, out IEnumerable<PaymentMethod>? cached) && cached != null)
            {
                return cached;
            }
        }
        catch { }

        var methods = await _context.PaymentMethods
            .AsNoTracking()
            .Where(p => !p.IsDeleted)
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .ToListAsync();

        var result = methods ?? (IEnumerable<PaymentMethod>)Enumerable.Empty<PaymentMethod>();

        try
        {
            _cache?.Set(CacheKeys.AllPaymentMethods, result, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CacheDuration,
                Size = 1
            });
        }
        catch { }

        return result;
    }

    public async Task<PaymentMethod> GetByIdAsync(int id)
    {
        var method = await _context.PaymentMethods.FindAsync(id);
        if (method == null || method.IsDeleted) throw new KeyNotFoundException($"Método de pago con ID {id} no fue encontrado.");
        return method;
    }

    public async Task<PaymentMethod> CreateAsync(PaymentMethod method)
    {
        if (method == null) throw new ArgumentNullException(nameof(method));
        if (string.IsNullOrWhiteSpace(method.Name))
            throw new ArgumentException("El nombre del método de pago es requerido.");

        var cleanName = method.Name.Trim();

        // Validación en memoria previa
        bool exists = await _context.PaymentMethods
            .AnyAsync(p => !p.IsDeleted && p.Name.ToLower() == cleanName.ToLower());
        if (exists)
        {
            throw new ArgumentException($"Ya existe un método de pago activo con el nombre '{cleanName}'.");
        }

        method.Name = cleanName;
        method.IsDeleted = false;

        // Asignación atómica de DisplayOrder bajo concurrencia
        if (method.DisplayOrder <= 0)
        {
            await AssignNextDisplayOrderAsync(method);
        }

        _context.PaymentMethods.Add(method);

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException dbEx) when (IsUniqueConstraintViolation(dbEx))
        {
            throw new ArgumentException($"Ya existe un método de pago activo con el nombre '{cleanName}'.");
        }

        InvalidateCache();
        if (_notifier != null)
        {
            await _notifier.NotifyPaymentMethodsUpdatedAsync();
        }
        return method;
    }

    public async Task<PaymentMethod> UpdateAsync(PaymentMethod method)
    {
        if (method == null) throw new ArgumentNullException(nameof(method));
        var existing = await _context.PaymentMethods.FindAsync(method.Id);
        if (existing == null) throw new KeyNotFoundException($"El método de pago con ID {method.Id} no fue encontrado.");
        if (existing.IsDeleted) throw new InvalidOperationException($"No se puede modificar el método de pago '{existing.Name}' porque ha sido eliminado.");

        if (string.IsNullOrWhiteSpace(method.Name))
            throw new ArgumentException("El nombre del método de pago es requerido.");

        var cleanName = method.Name.Trim();

        // Validación de unicidad de nombre excluyendo el método actual
        bool exists = await _context.PaymentMethods
            .AnyAsync(p => !p.IsDeleted && p.Id != method.Id && p.Name.ToLower() == cleanName.ToLower());
        if (exists)
        {
            throw new ArgumentException($"Ya existe otro método de pago activo con el nombre '{cleanName}'.");
        }

        existing.Name = cleanName;
        existing.IsActive = method.IsActive;
        existing.RequiresReference = method.RequiresReference;
        existing.DisplayOrder = method.DisplayOrder;
        existing.IsCash = method.IsCash;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidOperationException("El método de pago fue modificado concurrentemente por otro usuario. Por favor recargue la lista e intente de nuevo.");
        }
        catch (DbUpdateException dbEx) when (IsUniqueConstraintViolation(dbEx))
        {
            throw new ArgumentException($"Ya existe otro método de pago activo con el nombre '{cleanName}'.");
        }

        InvalidateCache();
        if (_notifier != null)
        {
            await _notifier.NotifyPaymentMethodsUpdatedAsync();
        }
        return existing;
    }

    public async Task DeleteAsync(int id)
    {
        var existing = await _context.PaymentMethods.FindAsync(id);
        if (existing == null) throw new KeyNotFoundException($"El método de pago con ID {id} no fue encontrado.");
        if (existing.IsDeleted) throw new InvalidOperationException($"El método de pago '{existing.Name}' ya fue eliminado previamente.");

        bool hasSales = await _context.SalePayments.AnyAsync(sp => sp.PaymentMethodId == id);
        bool hasClosures = await _context.ClosureDetails.AnyAsync(cd => cd.PaymentMethodId == id);

        // [MEDIDA TEMPORAL MT-001]: Regla conservadora de auditoría física de gaveta
        // CashTransactions no posee clave foránea PaymentMethodId directa y modela la gaveta con IsPhysicalCash = true.
        // Si el método es físico (IsCash = true) y existe cualquier movimiento físico en gaveta, se asume que fue utilizado y se aplica Soft Delete.
        // Ver registro en docs/medidas-temporales.txt.
        bool hasCashTransactions = existing.IsCash && await _context.CashTransactions.AnyAsync(ct => ct.IsPhysicalCash);

        bool hasBeenUsed = hasSales || hasClosures || hasCashTransactions;

        if (hasBeenUsed)
        {
            existing.IsActive = false;
            existing.IsDeleted = true; // Soft delete
        }
        else
        {
            _context.PaymentMethods.Remove(existing); // Hard delete
        }

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new InvalidOperationException("El método de pago fue modificado o eliminado concurrentemente por otro usuario.");
        }

        InvalidateCache();
        if (_notifier != null)
        {
            await _notifier.NotifyPaymentMethodsUpdatedAsync();
        }
    }

    public void InvalidateCache()
    {
        _cache?.Remove(CacheKeys.ActivePaymentMethods);
        _cache?.Remove(CacheKeys.AllPaymentMethods);
    }

    private async Task AssignNextDisplayOrderAsync(PaymentMethod method)
    {
        if (_context.Database.IsRelational())
        {
            try
            {
                using var cmd = _context.Database.GetDbConnection().CreateCommand();
                cmd.CommandText = "SELECT nextval('paymentmethod_displayorder_seq');";
                if (_context.Database.CurrentTransaction != null)
                {
                    cmd.Transaction = _context.Database.CurrentTransaction.GetDbTransaction();
                }
                if (cmd.Connection?.State != System.Data.ConnectionState.Open)
                {
                    await _context.Database.OpenConnectionAsync();
                }
                var nextVal = await cmd.ExecuteScalarAsync();
                if (nextVal != null && Convert.ToInt32(nextVal) > 0)
                {
                    method.DisplayOrder = Convert.ToInt32(nextVal);
                    return;
                }
            }
            catch
            {
                // Fallback defensivo si la secuencia no está sembrada o no está disponible
            }
        }

        method.DisplayOrder = (await _context.PaymentMethods.Where(p => !p.IsDeleted).MaxAsync(p => (int?)p.DisplayOrder) ?? 0) + 1;
    }

    private static bool IsUniqueConstraintViolation(DbUpdateException ex)
    {
        if (ex.InnerException is Npgsql.PostgresException pgEx && pgEx.SqlState == "23505")
        {
            return true;
        }
        return ex.InnerException?.Message.Contains("23505") == true || ex.Message.Contains("23505");
    }
}
