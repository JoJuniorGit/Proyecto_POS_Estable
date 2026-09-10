using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Core.Events;
using Core.Helpers;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Sales.Module.DTOs;
using Sales.Module.Entities;

namespace Sales.Module.Services;

public partial class SalesService
{

    public async Task<CustomerDto> GetDefaultCustomerAsync()
    {
        try
        {
            if (_cache != null && _cache.TryGetValue(DefaultCustomerCacheKey, out CustomerDto? cachedCustomer) && cachedCustomer != null)
            {
                return cachedCustomer;
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[SalesService] Fallo al leer el cliente por defecto desde la caché; se consulta a la base de datos.");
        }

        var defaultCustomer = await _context.Customers
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.IsDefault)
            ?? await _context.Customers
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == 1);

        if (defaultCustomer == null) throw new KeyNotFoundException("Cliente por defecto no encontrado.");
        
        var dto = new CustomerDto
        {
            Id = defaultCustomer.Id,
            CedulaOrRif = defaultCustomer.CedulaOrRif,
            Name = defaultCustomer.Name,
            Phone = defaultCustomer.Phone,
            CreditLimitUSD = defaultCustomer.CreditLimitUSD,
            IsActive = defaultCustomer.IsActive,
            IsDefault = defaultCustomer.IsDefault
        };

        try
        {
            _cache?.Set(DefaultCustomerCacheKey, dto, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1),
                Size = 1
            });
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[SalesService] Fallo al escribir el cliente por defecto en la caché.");
        }

        return dto;
    }

    public async Task<SaleDto> UpdateSaleCustomerAsync(int saleId, int customerId)
    {
        var sale = await GetSaleEntityAsync(saleId);
        
        if (sale.Status == SaleStatus.Completed || sale.Status == SaleStatus.Cancelled)
            throw new InvalidOperationException("No se puede modificar el cliente de una venta finalizada.");
            
        if (sale.Status == SaleStatus.OnHold && sale.Payments.Any())
            throw new InvalidOperationException("Una cuenta abierta con pagos registrados no permite cambio de titular.");

        var customer = await _context.Customers.FindAsync(customerId);
        if (customer == null) throw new KeyNotFoundException($"Cliente con ID {customerId} no encontrado.");

        sale.CustomerId = customer.Id;
        sale.CustomerName = customer.Name;
        sale.CustomerCedula = customer.CedulaOrRif;

        await _context.SaveChangesAsync();
        return MapToDto(sale);
    }


    public async Task<(IEnumerable<CustomerDto> Items, int TotalCount)> GetCustomersAsync(
        string? query = null,
        int page = 1,
        int pageSize = 20,
        bool recentOnly = false)
    {
        if (recentOnly)
        {
            var recentCustomerIds = await _context.Sales
                .AsNoTracking()
                .Where(s => s.CustomerId.HasValue)
                .GroupBy(s => s.CustomerId!.Value)
                .OrderByDescending(g => g.Max(s => s.Date))
                .Select(g => g.Key)
                .Take(3)
                .ToListAsync();

            var recentCustomers = await _context.Customers
                .AsNoTracking()
                .Where(c => recentCustomerIds.Contains(c.Id))
                .ToListAsync();

            if (recentCustomers.Count < 3)
            {
                var existingIds = recentCustomers.Select(c => c.Id).ToList();
                var additional = await _context.Customers
                    .AsNoTracking()
                    .Where(c => !existingIds.Contains(c.Id))
                    .OrderByDescending(c => c.Id)
                    .Take(3 - recentCustomers.Count)
                    .ToListAsync();
                recentCustomers.AddRange(additional);
            }

            var ordered = recentCustomerIds
                .Select(id => recentCustomers.FirstOrDefault(c => c.Id == id))
                .Where(c => c != null)
                .Concat(recentCustomers.Where(c => !recentCustomerIds.Contains(c.Id)))
                .DistinctBy(c => c!.Id)
                .Take(3)
                .Select(c => new CustomerDto
                {
                    Id = c!.Id,
                    CedulaOrRif = c.CedulaOrRif,
                    Name = c.Name,
                    Phone = c.Phone,
                    CreditLimitUSD = c.CreditLimitUSD,
                    IsActive = c.IsActive,
                    IsDefault = c.IsDefault
                })
                .ToList();

            return (ordered, ordered.Count);
        }

        var q = _context.Customers.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(query))
        {
            var lower = query.Trim().ToLower();
            q = q.Where(c => c.Name.ToLower().Contains(lower) || c.CedulaOrRif.ToLower().Contains(lower));
        }

        int totalCount = await q.CountAsync();
        page = Math.Max(1, page);
        pageSize = Math.Max(1, pageSize);

        var customers = await q
            .OrderBy(c => c.Name)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(c => new CustomerDto
            {
                Id = c.Id,
                CedulaOrRif = c.CedulaOrRif,
                Name = c.Name,
                Phone = c.Phone,
                CreditLimitUSD = c.CreditLimitUSD,
                IsActive = c.IsActive,
                IsDefault = c.IsDefault
            })
            .ToListAsync();

        return (customers, totalCount);
    }

    public async Task<CustomerDto> CreateCustomerAsync(CreateCustomerDto request)
    {
        if (string.IsNullOrWhiteSpace(request.CedulaOrRif) || string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Cédula/RIF y Nombre son campos obligatorios.");

        var normalizedCedula = request.CedulaOrRif.Trim().ToUpperInvariant();
        var exists = await _context.Customers.AnyAsync(c => c.CedulaOrRif.ToUpper() == normalizedCedula);
        if (exists)
            throw new InvalidOperationException($"Ya existe un cliente registrado con la Cédula/RIF '{request.CedulaOrRif}'.");

        var customer = new Customer
        {
            CedulaOrRif = normalizedCedula,
            Name = request.Name.Trim(),
            Phone = request.Phone?.Trim() ?? string.Empty,
            CreditLimitUSD = request.CreditLimitUSD >= 0 ? request.CreditLimitUSD : 0m,
            IsActive = true
        };

        _context.Customers.Add(customer);
        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            throw new InvalidOperationException($"Ya existe un cliente registrado con la Cédula/RIF '{request.CedulaOrRif}'.", ex);
        }

        return new CustomerDto
        {
            Id = customer.Id,
            CedulaOrRif = customer.CedulaOrRif,
            Name = customer.Name,
            Phone = customer.Phone,
            CreditLimitUSD = customer.CreditLimitUSD,
            IsActive = customer.IsActive
        };
    }

    public async Task<CustomerDto> UpdateCustomerAsync(int id, UpdateCustomerDto request)
    {
        var customer = await _context.Customers.FindAsync(id);
        if (customer == null)
            throw new KeyNotFoundException($"No se encontró el cliente con ID {id}.");

        if (customer.IsDefault && customer.CedulaOrRif.Equals("V-00000000", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(customer.CedulaOrRif, request.CedulaOrRif?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("No se permite cambiar la Cédula/RIF del cliente Consumidor Final.");
            }
        }

        if (string.IsNullOrWhiteSpace(request.CedulaOrRif) || string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Cédula/RIF y Nombre son campos obligatorios.");

        var normalizedCedula = request.CedulaOrRif.Trim().ToUpperInvariant();
        var exists = await _context.Customers.AnyAsync(c => c.Id != id && c.CedulaOrRif.ToUpper() == normalizedCedula);
        if (exists)
            throw new InvalidOperationException($"Ya existe otro cliente registrado con la Cédula/RIF '{request.CedulaOrRif}'.");

        customer.CedulaOrRif = normalizedCedula;
        customer.Name = request.Name.Trim();
        customer.Phone = request.Phone?.Trim() ?? string.Empty;
        customer.CreditLimitUSD = request.CreditLimitUSD >= 0 ? request.CreditLimitUSD : 0m;
        customer.IsActive = request.IsActive;

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (DbUpdateException ex)
        {
            throw new InvalidOperationException($"Ya existe otro cliente registrado con la Cédula/RIF '{request.CedulaOrRif}'.", ex);
        }

        if (customer.IsDefault || id == 1)
        {
            _cache?.Remove(DefaultCustomerCacheKey);
        }

        return new CustomerDto
        {
            Id = customer.Id,
            CedulaOrRif = customer.CedulaOrRif,
            Name = customer.Name,
            Phone = customer.Phone,
            CreditLimitUSD = customer.CreditLimitUSD,
            IsActive = customer.IsActive,
            IsDefault = customer.IsDefault
        };
    }

    public async Task DeleteCustomerAsync(int id)
    {
        var customer = await _context.Customers.FindAsync(id);
        if (customer == null)
            throw new KeyNotFoundException($"No se encontró el cliente con ID {id}.");

        if (customer.IsDefault || customer.CedulaOrRif.Equals("V-00000000", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("No se permite eliminar el cliente Consumidor Final predeterminado del sistema.");
        }

        bool hasSales = await _context.Sales.AnyAsync(s => s.CustomerId == id);
        if (hasSales)
        {
            throw new InvalidOperationException("No se puede eliminar el cliente porque tiene ventas o transacciones asociadas.");
        }

        _context.Customers.Remove(customer);
        await _context.SaveChangesAsync();
        _cache?.Remove(DefaultCustomerCacheKey);
    }

}
