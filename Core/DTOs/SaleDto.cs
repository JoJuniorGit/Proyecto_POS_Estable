using System;
using System.Collections.Generic;

namespace Core.DTOs;

public class SaleDto
{
    public int Id { get; set; }
    public int? InvoiceNumber { get; set; }
    public DateTime Date { get; set; }
    public string Status { get; set; } = string.Empty;
    public decimal Subtotal { get; set; }
    public decimal TotalUSD { get; set; }
    public decimal AppliedRate { get; set; }
    public decimal TotalBsS { get; set; }
    public decimal SubtotalBsS { get; set; }
    public decimal RoundingAdjustment { get; set; }
    public decimal FinalPaidAmountBsS { get; set; }
    public int? CashierId { get; set; }
    public string CashierName { get; set; } = "Usuario Desconocido";

    public int? CustomerId { get; set; }
    public CustomerDto? Customer { get; set; }
    public string? CustomerName { get; set; }
    public string? CustomerCedula { get; set; }
    public string DeliveryStatus { get; set; } = "Delivered";
    public DateTime? PickupDate { get; set; }
    public string PriceListType { get; set; } = "Retail";

    public decimal TotalPaidUSD { get; set; }
    public decimal RemainingBalanceUSD { get; set; }

    public List<SaleItemDto> Items { get; set; } = new();
    public List<SalePaymentDto> Payments { get; set; } = new();
}

public class SaleItemDto
{
    public int Id { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public bool IsFractional { get; set; }
    public Core.Entities.UnitOfMeasureType UnitOfMeasure { get; set; } = Core.Entities.UnitOfMeasureType.Und;
    public decimal UnitPrice { get; set; }
    public decimal Subtotal { get; set; }
    public decimal UnitPriceBsS { get; set; }
    public decimal SubtotalBsS { get; set; }
    public bool IsWholesaleApplied { get; set; }

    public string DisplayProductName => UnitOfMeasure != Core.Entities.UnitOfMeasureType.Und
        ? $"{ProductName} ({UnitOfMeasure})"
        : ProductName;
}

public class UpdatePriceListRequestDto
{
    public string PriceListType { get; set; } = "Retail";
}

public class SalePaymentDto
{
    public int Id { get; set; }
    public int PaymentMethodId { get; set; }
    public string PaymentMethodName { get; set; } = string.Empty;
    public decimal Amount { get; set; } // Amount in USD

    private decimal _amountBsS;
    private bool _amountBsSExplicitlySet;

    public decimal AmountBsS
    {
        get => _amountBsS;
        set
        {
            _amountBsS = value;
            if (value > 0) _amountBsSExplicitlySet = true;
        }
    }

    public decimal AmountLocal
    {
        get => _amountBsS;
        set
        {
            if (!_amountBsSExplicitlySet || _amountBsS == 0)
            {
                _amountBsS = value;
            }
        }
    }

    public decimal ExchangeRate { get; set; }
    public string? ReferenceNumber { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class HoldSaleRequestDto
{
    public int CustomerId { get; set; }
    public decimal ExchangeRate { get; set; }
    public bool IsProductDelivered { get; set; } = true;
    public AddPaymentRequestDto? InitialPayment { get; set; }
    public List<AddPaymentRequestDto>? InitialPayments { get; set; }
}

public class AddPaymentRequestDto
{
    public int PaymentMethodId { get; set; }
    public decimal AmountBsS { get; set; }
    public decimal AmountUSD { get; set; }
    public decimal ExchangeRate { get; set; }
    public string? ReferenceNumber { get; set; }
}
