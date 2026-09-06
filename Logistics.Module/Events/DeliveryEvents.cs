using System;
using MediatR;
using Core.Entities;

namespace Logistics.Module.Events;

/// <summary>
/// Event emitted when a delivery order is assigned to a driver.
/// </summary>
public record DeliveryAssignedEvent(
    int OrderId,
    int DriverId,
    DateTime AssignedAtUtc) : INotification;

/// <summary>
/// Event emitted when the delivery status changes (e.g. OutForDelivery, Delivered, Cancelled).
/// </summary>
public record DeliveryStatusUpdatedEvent(
    int OrderId,
    OrderStatus PreviousStatus,
    OrderStatus NewStatus,
    string? Note,
    DateTime UpdatedAtUtc) : INotification;
