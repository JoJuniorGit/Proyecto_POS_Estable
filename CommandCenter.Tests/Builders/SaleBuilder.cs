using System;
using System.Collections.Generic;
using System.Linq;
using Sales.Module.Entities;

namespace CommandCenter.Tests.Builders;

public class SaleBuilder
{
    private int _id = 1;
    private int? _invoiceNumber = null;
    private int? _customerId = null;
    private string? _customerName = null;
    private string? _customerCedula = null;
    private decimal _appliedRate = 50.00m;
    private SaleStatus _status = SaleStatus.Pending;
    private SaleDeliveryStatus _deliveryStatus = SaleDeliveryStatus.Delivered;
    private List<SaleItem> _items = new();
    private List<SalePayment> _payments = new();
    private decimal _roundingAdjustment = 0m;

    public SaleBuilder WithId(int id) { _id = id; return this; }
    public SaleBuilder WithInvoiceNumber(int invoiceNumber) { _invoiceNumber = invoiceNumber; return this; }
    public SaleBuilder WithCustomer(int id, string name, string cedula)
    {
        _customerId = id;
        _customerName = name;
        _customerCedula = cedula;
        return this;
    }
    public SaleBuilder WithAppliedRate(decimal rate) { _appliedRate = rate; return this; }
    public SaleBuilder WithStatus(SaleStatus status) { _status = status; return this; }
    public SaleBuilder WithDeliveryStatus(SaleDeliveryStatus deliveryStatus) { _deliveryStatus = deliveryStatus; return this; }
    public SaleBuilder WithRoundingAdjustment(decimal rounding) { _roundingAdjustment = rounding; return this; }

    public SaleBuilder WithItem(int productId, string name, decimal qty, decimal unitPriceUsd)
    {
        _items.Add(new SaleItem
        {
            Id = _items.Count + 1,
            ProductId = productId,
            ProductName = name,
            Quantity = qty,
            UnitPrice = unitPriceUsd,
            UnitPriceBsS = Math.Round(unitPriceUsd * _appliedRate, 2, MidpointRounding.AwayFromZero),
            Subtotal = Math.Round(qty * unitPriceUsd, 2, MidpointRounding.AwayFromZero),
            SubtotalBsS = Math.Round(qty * unitPriceUsd * _appliedRate, 2, MidpointRounding.AwayFromZero)
        });
        return this;
    }

    public SaleBuilder WithPayment(int paymentMethodId, decimal amountUsd, decimal? amountBsS = null, string? reference = null)
    {
        decimal bsS = amountBsS ?? Math.Round(amountUsd * _appliedRate, 2, MidpointRounding.AwayFromZero);
        _payments.Add(new SalePayment
        {
            Id = _payments.Count + 1,
            PaymentMethodId = paymentMethodId,
            Amount = amountUsd,
            AmountBsS = bsS,
            ExchangeRate = _appliedRate,
            ReferenceNumber = reference,
            CreatedAt = DateTime.UtcNow
        });
        return this;
    }

    public Sale Build()
    {
        decimal subtotalUsd = _items.Sum(i => i.Subtotal);
        decimal subtotalBsS = _items.Sum(i => i.SubtotalBsS);

        var sale = new Sale
        {
            Id = _id,
            InvoiceNumber = _invoiceNumber,
            CustomerId = _customerId,
            CustomerName = _customerName,
            CustomerCedula = _customerCedula,
            Date = DateTime.UtcNow,
            AppliedRate = _appliedRate,
            Status = _status,
            DeliveryStatus = _deliveryStatus,
            Subtotal = subtotalUsd,
            TotalUSD = subtotalUsd,
            SubtotalBsS = subtotalBsS,
            TotalBsS = subtotalBsS,
            RoundingAdjustment = _roundingAdjustment,
            FinalPaidAmountBsS = _payments.Sum(p => p.AmountBsS),
            Items = _items,
            Payments = _payments
        };

        foreach (var item in _items) item.SaleId = _id;
        foreach (var payment in _payments) payment.SaleId = _id;

        return sale;
    }
}
