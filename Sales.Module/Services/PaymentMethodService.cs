using Core.Constants;
using Sales.Module.DTOs;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
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

    private static PaymentMethodDto ToDto(PaymentMethod method)
    {
        return new PaymentMethodDto
        {
            Id = method.Id,
            Name = method.Name,
            IsActive = method.IsActive,
            RequiresReference = method.RequiresReference,
            IsCash = method.IsCash,
            Currency = method.Currency,
            DisplayOrder = method.DisplayOrder,
            IsDeleted = method.IsDeleted
        };
    }

    public async Task<IEnumerable<PaymentMethodDto>> GetActiveMethodsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_cache != null && _cache.TryGetValue(CacheKeys.ActivePaymentMethods, out IEnumerable<PaymentMethodDto>? cached) && cached != null)
            {
                return cached;
            }
        }
        catch (Exception ex)
        {
            Core.Logging.AppLogger.LogWarn($"[PaymentMethodService] Fallo al leer métodos de pago activos desde la caché; se consulta a la base de datos. {ex.Message}");
        }

        var methods = await _context.PaymentMethods
            .AsNoTracking()
            .Where(p => !p.IsDeleted && p.IsActive)
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .Select(p => new PaymentMethodDto
            {
                Id = p.Id,
                Name = p.Name,
                IsActive = p.IsActive,
                RequiresReference = p.RequiresReference,
                IsCash = p.IsCash,
                Currency = p.Currency,
                DisplayOrder = p.DisplayOrder,
                IsDeleted = p.IsDeleted
            })
            .ToListAsync(cancellationToken);

        var result = (IEnumerable<PaymentMethodDto>)(methods ?? new List<PaymentMethodDto>());

        try
        {
            _cache?.Set(CacheKeys.ActivePaymentMethods, result, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CacheDuration,
                Size = 1
            });
        }
        catch (Exception ex)
        {
            Core.Logging.AppLogger.LogWarn($"[PaymentMethodService] Fallo al escribir métodos de pago activos en la caché. {ex.Message}");
        }

        return result;
    }

    public async Task<IEnumerable<PaymentMethodDto>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (_cache != null && _cache.TryGetValue(CacheKeys.AllPaymentMethods, out IEnumerable<PaymentMethodDto>? cached) && cached != null)
            {
                return cached;
            }
        }
        catch (Exception ex)
        {
            Core.Logging.AppLogger.LogWarn($"[PaymentMethodService] Fallo al leer métodos de pago desde la caché; se consulta a la base de datos. {ex.Message}");
        }

        var methods = await _context.PaymentMethods
            .AsNoTracking()
            .Where(p => !p.IsDeleted)
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .Select(p => new PaymentMethodDto
            {
                Id = p.Id,
                Name = p.Name,
                IsActive = p.IsActive,
                RequiresReference = p.RequiresReference,
                IsCash = p.IsCash,
                Currency = p.Currency,
                DisplayOrder = p.DisplayOrder,
                IsDeleted = p.IsDeleted
            })
            .ToListAsync(cancellationToken);

        var result = (IEnumerable<PaymentMethodDto>)(methods ?? new List<PaymentMethodDto>());

        try
        {
            _cache?.Set(CacheKeys.AllPaymentMethods, result, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = CacheDuration,
                Size = 1
            });
        }
        catch (Exception ex)
        {
            Core.Logging.AppLogger.LogWarn($"[PaymentMethodService] Fallo al escribir métodos de pago en la caché. {ex.Message}");
        }

        return result;
    }

    public async Task<PaymentMethodDto> GetByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        var method = await _context.PaymentMethods
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && !p.IsDeleted, cancellationToken);
        if (method == null) throw new KeyNotFoundException($"Método de pago con ID {id} no fue encontrado.");
        return ToDto(method);
    }

    public async Task<PaymentMethodDto> CreateAsync(PaymentMethodDto dto, CancellationToken cancellationToken = default)
    {
        if (dto == null) throw new ArgumentNullException(nameof(dto));
        if (string.IsNullOrWhiteSpace(dto.Name))
            throw new ArgumentException("El nombre del método de pago es requerido.");

        var cleanName = dto.Name.Trim();

        bool exists = await _context.PaymentMethods
            .AnyAsync(p => !p.IsDeleted && p.Name.ToLower() == cleanName.ToLower(), cancellationToken);
        if (exists)
        {
            throw new ArgumentException($"Ya existe un método de pago activo con el nombre '{cleanName}'.");
        }

        var entity = new PaymentMethod
        {
            Name = cleanName,
            IsActive = dto.IsActive,
            RequiresReference = dto.RequiresReference,
            IsCash = dto.IsCash,
            DisplayOrder = dto.DisplayOrder,
            IsDeleted = false
        };

        if (entity.DisplayOrder <= 0)
        {
            await AssignNextDisplayOrderAsync(entity, cancellationToken);
        }

        _context.PaymentMethods.Add(entity);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
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
        return ToDto(entity);
    }

    public Task<PaymentMethodDto> CreateAsync(PaymentMethod method, CancellationToken cancellationToken = default)
    {
        if (method == null) throw new ArgumentNullException(nameof(method));
        return CreateAsync(ToDto(method), cancellationToken);
    }

    public async Task<PaymentMethodDto> UpdateAsync(PaymentMethodDto dto, CancellationToken cancellationToken = default)
    {
        if (dto == null) throw new ArgumentNullException(nameof(dto));
        var existing = await _context.PaymentMethods.FindAsync(new object[] { dto.Id }, cancellationToken);
        if (existing == null) throw new KeyNotFoundException($"El método de pago con ID {dto.Id} no fue encontrado.");
        if (existing.IsDeleted) throw new InvalidOperationException($"No se puede modificar el método de pago '{existing.Name}' porque ha sido eliminado.");

        if (string.IsNullOrWhiteSpace(dto.Name))
            throw new ArgumentException("El nombre del método de pago es requerido.");

        var cleanName = dto.Name.Trim();

        bool exists = await _context.PaymentMethods
            .AnyAsync(p => !p.IsDeleted && p.Id != dto.Id && p.Name.ToLower() == cleanName.ToLower(), cancellationToken);
        if (exists)
        {
            throw new ArgumentException($"Ya existe otro método de pago activo con el nombre '{cleanName}'.");
        }

        existing.Name = cleanName;
        existing.IsActive = dto.IsActive;
        existing.RequiresReference = dto.RequiresReference;
        existing.DisplayOrder = dto.DisplayOrder;
        existing.IsCash = dto.IsCash;

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
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
        return ToDto(existing);
    }

    public Task<PaymentMethodDto> UpdateAsync(PaymentMethod method, CancellationToken cancellationToken = default)
    {
        if (method == null) throw new ArgumentNullException(nameof(method));
        return UpdateAsync(ToDto(method), cancellationToken);
    }

    public async Task DeleteAsync(int id, CancellationToken cancellationToken = default)
    {
        var existing = await _context.PaymentMethods.FindAsync(new object[] { id }, cancellationToken);
        if (existing == null) throw new KeyNotFoundException($"El método de pago con ID {id} no fue encontrado.");
        if (existing.IsDeleted) throw new InvalidOperationException($"El método de pago '{existing.Name}' ya fue eliminado previamente.");

        bool hasSales = await _context.SalePayments.AnyAsync(sp => sp.PaymentMethodId == id, cancellationToken);
        bool hasClosures = await _context.ClosureDetails.AnyAsync(cd => cd.PaymentMethodId == id, cancellationToken);
        bool hasCashTransactions = await _context.CashTransactions.AnyAsync(ct => ct.PaymentMethodId == id, cancellationToken);

        bool hasBeenUsed = hasSales || hasClosures || hasCashTransactions;

        if (hasBeenUsed)
        {
            existing.IsActive = false;
            existing.IsDeleted = true;
        }
        else
        {
            _context.PaymentMethods.Remove(existing);
        }

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
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

    private async Task AssignNextDisplayOrderAsync(PaymentMethod method, CancellationToken cancellationToken = default)
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
                    await _context.Database.OpenConnectionAsync(cancellationToken);
                }
                var nextVal = await cmd.ExecuteScalarAsync(cancellationToken);
                if (nextVal != null && Convert.ToInt32(nextVal) > 0)
                {
                    method.DisplayOrder = Convert.ToInt32(nextVal);
                    return;
                }
            }
            catch (Exception ex)
            {
                Core.Logging.AppLogger.LogWarn($"No se pudo obtener el siguiente valor de secuencia para DisplayOrder, usando fallback: {ex.Message}");
            }
        }

        method.DisplayOrder = (await _context.PaymentMethods.Where(p => !p.IsDeleted).MaxAsync(p => (int?)p.DisplayOrder, cancellationToken) ?? 0) + 1;
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
