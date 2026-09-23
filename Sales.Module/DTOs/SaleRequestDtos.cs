using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using Core.DTOs;

namespace Sales.Module.DTOs;

public class AddItemRequest
{
    public int ProductId { get; set; }
    public decimal Quantity { get; set; }
    public decimal ExchangeRate { get; set; }
    public decimal? CustomUnitPriceUsd { get; set; }
    public decimal? CustomUnitPriceLocal { get; set; }
}

public class UpdateQuantityRequest
{
    public decimal Quantity { get; set; }
    public decimal ExchangeRate { get; set; }
}

public class CompleteSaleRequest
{
    public decimal ExchangeRate { get; set; }
    // 8.7-B2: El ajuste de redondeo debe acotarse; la validación en el servicio refuerza el límite.
    [Range(-1000, 1000, ErrorMessage = "El ajuste de redondeo está fuera de los límites operacionales (-1000 a 1000).")]
    public decimal RoundingAdjustment { get; set; }
    public int? CashierId { get; set; }
    public bool IsPendingPickup { get; set; } = false;
    public IEnumerable<SalePaymentDto> Payments { get; set; } = new List<SalePaymentDto>();
}

public class UpdateSaleCustomerRequest
{
    public int CustomerId { get; set; }
}

public class CheckoutPreviewRequest
{
    public decimal ExchangeRate { get; set; }
    public IEnumerable<SalePaymentDto> Payments { get; set; } = new List<SalePaymentDto>();
}

public class CheckoutPreviewResponse
{
    public decimal TotalUSD { get; set; }
    public decimal TotalBsS { get; set; }
    public decimal TotalPaidUSD { get; set; }
    public decimal TotalPaidBsS { get; set; }
    public decimal RemainingBalanceUSD { get; set; }
    public decimal RemainingBalanceBsS { get; set; }
    public decimal RoundingAdjustment { get; set; }
    public decimal ChangeDueUSD { get; set; }
    public decimal ChangeDueBsS { get; set; }
    public bool IsFullyPaid { get; set; }
}

public class ClaimSaleRequest
{
    public string Action { get; set; } = "Editing";
}
