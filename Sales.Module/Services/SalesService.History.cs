using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Core.DTOs;
using Core.Entities;
using Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Sales.Module.DTOs;
using Sales.Module.Entities;

namespace Sales.Module.Services;

public partial class SalesService
{
    public async Task<SaleHistoryDto> ConfirmPickupAsync(int saleId)
    {
        var _sale = await _context.Sales
            .AsSplitQuery()
            .Include(s => s.Customer)
            .Include(s => s.Items)
            .Include(s => s.Payments).ThenInclude(p => p.PaymentMethod)
            .FirstOrDefaultAsync(s => s.Id == saleId);

        if (_sale == null) throw new KeyNotFoundException("Sale not found.");

        if (_sale.DeliveryStatus != SaleDeliveryStatus.PendingPickup)
            throw new InvalidOperationException($"El pedido #{_sale.InvoiceNumber ?? _sale.Id} no se encuentra en estado Pendiente por Retirar.");

        _sale.DeliveryStatus = SaleDeliveryStatus.Delivered;
        _sale.PickupDate = DateTime.UtcNow;

        await _context.SaveChangesAsync();

        return await GetSaleHistoryDetailAsync(saleId);
    }

    // 8.9-B2: soporta scope por cajero (Cashier filtra sus entregas; Admin/Manager ven todas).
    // 8.9-B5: proyección directa en SQL (sin materializar entidades con Items/Payments completos).
    public async Task<IEnumerable<PendingPickupDto>> GetPendingPickupsAsync(int? cashierId = null)
    {
        var _sales = await _context.Sales
            .AsNoTracking()
            .AsSplitQuery()
            .Where(s => s.Status == SaleStatus.Completed && s.DeliveryStatus == SaleDeliveryStatus.PendingPickup)
            .Where(s => !cashierId.HasValue || s.CashierId == cashierId.Value)
            .OrderByDescending(s => s.Date)
            .Select(s => new PendingPickupDto
            {
                SaleId = s.Id,
                InvoiceNumber = s.InvoiceNumber,
                Date = s.Date,
                CustomerId = s.CustomerId,
                CustomerName = s.CustomerName ?? s.Customer!.Name ?? "Cliente Desconocido",
                CustomerCedula = s.CustomerCedula ?? s.Customer!.CedulaOrRif ?? "V-00000000",
                CustomerPhone = s.Customer != null ? s.Customer.Phone : string.Empty,
                TotalUSD = s.TotalUSD,
                TotalBsS = s.TotalBsS,
                DeliveryStatus = s.DeliveryStatus.ToString(),
                PickupDate = s.PickupDate,
                Items = s.Items.Select(i => new SaleItemHistoryDto
                {
                    Id = i.Id,
                    ProductName = i.ProductName,
                    Quantity = i.Quantity,
                    UnitPrice = i.UnitPrice,
                    UnitPriceBsS = i.UnitPriceBsS,
                    SubtotalBsS = i.SubtotalBsS
                }).ToList()
            })
            .ToListAsync();

        return _sales;
    }

    // 8.9-B2: un Cashier solo consulta su propio historial; Admin/Manager sin restricción.
    public async Task<(IEnumerable<SaleHistoryDto> Items, int TotalCount)> GetSalesHistoryAsync(int page, int pageSize, DateTime? startDate, DateTime? endDate, string? search = null, int? cashierId = null)
    {
        var query = _context.Sales
            .AsNoTracking()
            .Include(s => s.Cashier)
            .Include(s => s.Customer)
            .Where(s => s.Status == SaleStatus.Completed);

        // 8.9-B2: filtro de propiedad para el rol Cashier.
        if (cashierId.HasValue)
        {
            query = query.Where(s => s.CashierId == cashierId.Value);
        }

        // La columna Date es "timestamp with time zone" (UTC): Npgsql rechaza parámetros
        // DateTime con Kind != Utc. Se utiliza TimeZoneHelper.GetUtcRange para convertir las
        // fechas calendario locales de Venezuela (UTC-4) a límites exactos UTC de inicio y fin inclusivo del día,
        // garantizando que todas las ventas nocturnas (después de las 8:00 PM VET) sean incluidas correctamente.
        bool shouldDefaultEndDate = startDate.HasValue && !endDate.HasValue;
        var (startUtc, endExclusiveUtc) = TimeZoneHelper.GetUtcRange(startDate, endDate, defaultEndDateToToday: shouldDefaultEndDate);

        if (startUtc.HasValue) query = query.Where(s => s.Date >= startUtc.Value);
        if (endExclusiveUtc.HasValue) query = query.Where(s => s.Date < endExclusiveUtc.Value);

        // Búsqueda multicampo: coincidencia de texto (insensible a mayúsculas) simultánea
        // en N° de factura, cliente (nombre o cédula) y cajero (nombre, nombre completo o cédula).
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var lowerTerm = term.ToLower();
            var isNumericTerm = int.TryParse(term, out var invoiceMatch);

            query = query.Where(s =>
                (s.CustomerName != null && s.CustomerName.ToLower().Contains(lowerTerm)) ||
                (s.CustomerCedula != null && s.CustomerCedula.ToLower().Contains(lowerTerm)) ||
                (s.Cashier != null &&
                 ((s.Cashier.Name != null && s.Cashier.Name.ToLower().Contains(lowerTerm)) ||
                  (s.Cashier.FullName != null && s.Cashier.FullName.ToLower().Contains(lowerTerm)) ||
                  (s.Cashier.Cedula != null && s.Cashier.Cedula.ToLower().Contains(lowerTerm)))) ||
                (s.InvoiceNumber != null && s.InvoiceNumber.Value.ToString().Contains(term)) ||
                (isNumericTerm && s.InvoiceNumber == invoiceMatch));
        }

        int totalCount = await query.CountAsync();

        var items = await query
            .OrderByDescending(s => s.Date)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new SaleHistoryDto
            {
                Id = s.Id,
                InvoiceNumber = s.InvoiceNumber,
                Date = s.Date,
                TotalUSD = s.TotalUSD,
                AppliedRate = s.AppliedRate,
                TotalBsS = s.TotalBsS,
                Status = s.Status.ToString(),
                FinalPaidAmountBsS = s.FinalPaidAmountBsS,
                CashierName = s.Cashier != null ? (!string.IsNullOrWhiteSpace(s.Cashier.Name) ? s.Cashier.Name : (!string.IsNullOrWhiteSpace(s.Cashier.FullName) ? s.Cashier.FullName : s.Cashier.Cedula)) : "Usuario Desconocido",
                CustomerName = s.CustomerName ?? (s.Customer != null ? s.Customer.Name : "Consumidor Final"),
                CustomerCedula = s.CustomerCedula ?? (s.Customer != null ? s.Customer.CedulaOrRif : "V-00000000"),
                DeliveryStatus = s.DeliveryStatus.ToString(),
                PickupDate = s.PickupDate
            })
            .ToListAsync();

        return (items, totalCount);
    }

    public async Task<SaleHistoryDto> GetSaleHistoryDetailAsync(int saleId)
    {
        var _sale = await _context.Sales
            .AsNoTracking()
            .AsSplitQuery()
            .Include(s => s.Items)
            .Include(s => s.Payments)
                .ThenInclude(p => p.PaymentMethod)
            .Include(s => s.Cashier)
            .Include(s => s.Customer)
            .FirstOrDefaultAsync(s => s.Id == saleId);

        if (_sale == null) throw new KeyNotFoundException($"Sale {saleId} not found.");

        return new SaleHistoryDto
        {
            Id = _sale.Id,
            InvoiceNumber = _sale.InvoiceNumber,
            Date = _sale.Date,
            TotalUSD = _sale.TotalUSD,
            AppliedRate = _sale.AppliedRate,
            TotalBsS = _sale.TotalBsS,
            Status = _sale.Status.ToString(),
            FinalPaidAmountBsS = _sale.FinalPaidAmountBsS,
            CashierName = _sale.Cashier != null ? (!string.IsNullOrWhiteSpace(_sale.Cashier.Name) ? _sale.Cashier.Name : (!string.IsNullOrWhiteSpace(_sale.Cashier.FullName) ? _sale.Cashier.FullName : _sale.Cashier.Cedula)) : "Usuario Desconocido",
            CustomerName = _sale.CustomerName ?? (_sale.Customer != null ? _sale.Customer.Name : "Consumidor Final"),
            CustomerCedula = _sale.CustomerCedula ?? (_sale.Customer != null ? _sale.Customer.CedulaOrRif : "V-00000000"),
            DeliveryStatus = _sale.DeliveryStatus.ToString(),
            PickupDate = _sale.PickupDate,
            Items = _sale.Items.Select(i => new SaleItemHistoryDto
            {
                Id = i.Id,
                ProductName = i.ProductName,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice,
                UnitPriceBsS = i.UnitPriceBsS,
                SubtotalBsS = i.SubtotalBsS
            }).ToList(),
            Payments = _sale.Payments.Select(p => new PaymentDetailDto
            {
                MethodName = p.PaymentMethod != null ? p.PaymentMethod.Name : "Desconocido",
                AmountBsS = p.AmountBsS,
                Reference = p.ReferenceNumber
            }).ToList()
        };
    }
}
