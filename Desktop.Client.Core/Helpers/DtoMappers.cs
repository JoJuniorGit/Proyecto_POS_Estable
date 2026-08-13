using Core.DTOs;
using Desktop.Client.Services;
using System.Linq;

namespace Desktop.Client.Helpers;

public static class DtoMappers
{
    public static SaleDto ToStandardDto(this SaleHistoryDto historyDto)
    {
        return new SaleDto
        {
            Id = historyDto.Id,
            InvoiceNumber = historyDto.InvoiceNumber,
            Date = historyDto.Date,
            Status = historyDto.Status,
            TotalUSD = historyDto.TotalUSD,
            AppliedRate = historyDto.AppliedRate,
            TotalBsS = historyDto.TotalBsS,
            FinalPaidAmountBsS = historyDto.FinalPaidAmountBsS,
            // Subtotals can be inferred or left 0 if not present in historyDto directly,
            // but for CartViewModel it will recalculate them anyway.
            Subtotal = historyDto.Items.Sum(i => i.Quantity * i.UnitPrice),
            SubtotalBsS = historyDto.Items.Sum(i => i.SubtotalBsS),
            Items = historyDto.Items.Select(i => new SaleItemDto
            {
                Id = i.Id,
                ProductId = 0, // ProductId is not needed for historical display
                ProductName = i.ProductName,
                Quantity = i.Quantity,
                UnitPrice = i.UnitPrice,
                Subtotal = i.Quantity * i.UnitPrice,
                UnitPriceBsS = i.UnitPriceBsS,
                SubtotalBsS = i.SubtotalBsS
            }).ToList()
        };
    }
}
