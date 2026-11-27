using Core.DTOs;
using Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

/// <summary>
/// 8.205 (split de SalesService por caso de uso): consultas de historial y entregas pendientes.
/// Es una superficie de solo lectura; las mutaciones de entrega siguen en SalesService.
/// </summary>
internal sealed class SalesHistoryService
{
    private readonly SalesDbContext _context;

    public SalesHistoryService(SalesDbContext context)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
    }

    public async Task<IEnumerable<PendingPickupDto>> GetPendingPickupsAsync(
        int? cashierId = null,
        int limit = 200,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        // 8.2-M9: tope de la cola (default 200, max 1000 en el controlador).
        // 8.14-N1: paginación real por offset.
        if (limit <= 0) limit = 200;
        if (offset < 0) offset = 0;

        var sales = await _context.Sales
            .AsNoTracking()
            .Where(s => s.Status == SaleStatus.Completed
                && (s.DeliveryStatus == SaleDeliveryStatus.PendingPickup
                    || s.DeliveryStatus == SaleDeliveryStatus.PartiallyDelivered))
            .Where(s => !cashierId.HasValue || s.CashierId == cashierId.Value)
            .OrderByDescending(s => s.Date)
            .ThenByDescending(s => s.Id)
            .Skip(offset)
            .Take(limit)
            .Select(s => new PendingPickupDto
            {
                SaleId = s.Id,
                InvoiceNumber = s.InvoiceNumber,
                Date = s.Date,
                CustomerId = s.CustomerId,
                CustomerName = s.CustomerName ?? (s.Customer != null ? s.Customer.Name : "Cliente Desconocido"),
                CustomerCedula = s.CustomerCedula ?? (s.Customer != null ? s.Customer.CedulaOrRif : "V-00000000"),
                CustomerPhone = s.Customer != null ? s.Customer.Phone : string.Empty,
                TotalUSD = s.TotalUSD,
                TotalBsS = s.TotalBsS,
                AppliedRate = s.AppliedRate,
                TotalUnits = s.Items.Sum(i => i.Quantity),
                DeliveredUnits = s.Items.Sum(i => i.DeliveredQuantity),
                DeliveryStatus = s.DeliveryStatus.ToString(),
                PickupDate = s.PickupDate,
                Items = s.Items.Select(i => new PendingPickupItemDto
                {
                    SaleItemId = i.Id,
                    ProductId = i.ProductId,
                    ProductName = i.ProductName,
                    Quantity = i.Quantity,
                    DeliveredQuantity = i.DeliveredQuantity,
                    PendingQuantity = i.Quantity - i.DeliveredQuantity,
                    UnitPrice = i.UnitPrice,
                    UnitPriceBsS = i.UnitPriceBsS,
                    SubtotalBsS = i.SubtotalBsS,
                    IsCustomPrice = i.IsCustomPrice
                }).ToList()
            })
            .ToListAsync(cancellationToken);

        return sales;
    }

    public async Task<int> CountPendingPickupsAsync(
        int? cashierId = null,
        CancellationToken cancellationToken = default)
    {
        return await _context.Sales
            .AsNoTracking()
            .CountAsync(s => s.Status == SaleStatus.Completed
                && (s.DeliveryStatus == SaleDeliveryStatus.PendingPickup
                    || s.DeliveryStatus == SaleDeliveryStatus.PartiallyDelivered)
                && (!cashierId.HasValue || s.CashierId == cashierId.Value), cancellationToken);
    }

    public async Task<(IEnumerable<SaleHistoryDto> Items, int TotalCount)> GetSalesHistoryAsync(
        int page,
        int pageSize,
        DateTime? startDate,
        DateTime? endDate,
        string? search = null,
        int? cashierId = null,
        CancellationToken cancellationToken = default)
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
        // fechas calendario locales de Venezuela (UTC-4) a límites exactos UTC de inicio y fin
        // inclusivo del día, garantizando que las ventas nocturnas se incluyan correctamente.
        bool shouldDefaultEndDate = startDate.HasValue && !endDate.HasValue;
        var (startUtc, endExclusiveUtc) = TimeZoneHelper.GetUtcRange(startDate, endDate, defaultEndDateToToday: shouldDefaultEndDate);

        if (startUtc.HasValue) query = query.Where(s => s.Date >= startUtc.Value);
        if (endExclusiveUtc.HasValue) query = query.Where(s => s.Date < endExclusiveUtc.Value);

        // Búsqueda multicampo: ILIKE para explotar pg_trgm, con comodines escapados.
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim();
            var pattern = ToLikePattern(term);
            var isNumericTerm = int.TryParse(term, out var invoiceMatch);

            query = query.Where(s =>
                (s.CustomerName != null && EF.Functions.ILike(s.CustomerName, pattern)) ||
                (s.CustomerCedula != null && EF.Functions.ILike(s.CustomerCedula, pattern)) ||
                (s.Cashier != null &&
                 ((s.Cashier.Name != null && EF.Functions.ILike(s.Cashier.Name, pattern)) ||
                  (s.Cashier.FullName != null && EF.Functions.ILike(s.Cashier.FullName, pattern)) ||
                  (s.Cashier.Cedula != null && EF.Functions.ILike(s.Cashier.Cedula, pattern)))) ||
                (s.InvoiceNumber != null && s.InvoiceNumber.Value.ToString().Contains(term)) ||
                (isNumericTerm && s.InvoiceNumber == invoiceMatch));
        }

        int totalCount = await query.CountAsync(cancellationToken);

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
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }

    public async Task<SaleHistoryDto> GetSaleHistoryDetailAsync(
        int saleId,
        CancellationToken cancellationToken = default)
    {
        var sale = await _context.Sales
            .AsNoTracking()
            .AsSplitQuery()
            .Include(s => s.Items)
            .Include(s => s.Payments)
                .ThenInclude(p => p.PaymentMethod)
            .Include(s => s.Cashier)
            .Include(s => s.Customer)
            .FirstOrDefaultAsync(s => s.Id == saleId, cancellationToken);

        if (sale == null) throw new KeyNotFoundException($"Sale {saleId} not found.");

        return MapToHistoryDetail(sale);
    }

    // 8.9-M17: mapeo único a SaleHistoryDto para que ConfirmPickup y el detalle produzcan
    // exactamente el mismo DTO.
    internal static SaleHistoryDto MapToHistoryDetail(Sale sale)
    {
        return new SaleHistoryDto
        {
            Id = sale.Id,
            InvoiceNumber = sale.InvoiceNumber,
            Date = sale.Date,
            TotalUSD = sale.TotalUSD,
            AppliedRate = sale.AppliedRate,
            TotalBsS = sale.TotalBsS,
            Status = sale.Status.ToString(),
            FinalPaidAmountBsS = sale.FinalPaidAmountBsS,
            CashierName = sale.Cashier != null ? (!string.IsNullOrWhiteSpace(sale.Cashier.Name) ? sale.Cashier.Name : (!string.IsNullOrWhiteSpace(sale.Cashier.FullName) ? sale.Cashier.FullName : sale.Cashier.Cedula)) : "Usuario Desconocido",
            CustomerName = sale.CustomerName ?? (sale.Customer != null ? sale.Customer.Name : "Consumidor Final"),
            CustomerCedula = sale.CustomerCedula ?? (sale.Customer != null ? sale.Customer.CedulaOrRif : "V-00000000"),
            DeliveryStatus = sale.DeliveryStatus.ToString(),
            PickupDate = sale.PickupDate,
            Items = sale.Items.Select(i => new SaleItemHistoryDto
            {
                Id = i.Id,
                ProductName = i.ProductName,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice,
                UnitPriceBsS = i.UnitPriceBsS,
                SubtotalBsS = i.SubtotalBsS,
                IsCustomPrice = i.IsCustomPrice
            }).ToList(),
            Payments = sale.Payments.Select(p => new PaymentDetailDto
            {
                MethodName = p.PaymentMethod != null ? p.PaymentMethod.Name : "Desconocido",
                AmountBsS = p.AmountBsS,
                Reference = p.ReferenceNumber
            }).ToList()
        };
    }

    // 8.9-M11: patrón para ILIKE "%term%" con los comodines LIKE del usuario escapados.
    private static string ToLikePattern(string term)
    {
        var escaped = term
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
        return $"%{escaped}%";
    }
}
