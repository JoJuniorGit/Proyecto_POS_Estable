using System;
using MediatR;

namespace Core.Events;

/// <summary>
/// Event published when a sale order is ready and dispatched for delivery,
/// allowing Logistics.Module to decouple from Sales.Module.
/// </summary>
public record SaleDispatchedEvent(
    int SaleId,
    string InvoiceNumber,
    string? CustomerName,
    string? DeliveryAddress,
    string? CustomerPhone,
    decimal TotalAmount,
    DateTime DispatchedAtUtc) : INotification;
