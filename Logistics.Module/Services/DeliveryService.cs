using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Entities;
using Logistics.Module.DTOs;
using Logistics.Module.Events;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Logistics.Module.Services;

public class DeliveryService : IDeliveryService
{
    private readonly ConcurrentDictionary<int, DeliveryOrderDto> _deliveries = new();
    private readonly IMediator _mediator;
    private readonly ILogger<DeliveryService>? _logger;

    public DeliveryService(IMediator mediator, ILogger<DeliveryService>? logger = null)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
        _logger = logger;
    }

    public Task<DeliveryOrderDto> RegisterDeliveryOrderAsync(DeliveryOrderDto order, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (order.OrderId <= 0)
        {
            throw new ArgumentException("El OrderId debe ser mayor a 0.", nameof(order));
        }

        _deliveries[order.OrderId] = order;
        _logger?.LogInformation("[Logistics] Orden de entrega #{OrderId} registrada para cliente '{Customer}' con destino '{Address}'.",
            order.OrderId, order.CustomerName, order.DeliveryAddress);

        return Task.FromResult(order);
    }

    public Task<DeliveryOrderDto?> GetDeliveryOrderAsync(int orderId, CancellationToken cancellationToken = default)
    {
        _deliveries.TryGetValue(orderId, out var order);
        return Task.FromResult(order);
    }

    public Task<IReadOnlyList<DeliveryOrderDto>> GetPendingDeliveriesAsync(CancellationToken cancellationToken = default)
    {
        var pending = _deliveries.Values
            .Where(d => d.Status == OrderStatus.Pending || d.Status == OrderStatus.Preparing || d.Status == OrderStatus.OutForDelivery)
            .OrderBy(d => d.CreatedAtUtc)
            .ToList();

        return Task.FromResult<IReadOnlyList<DeliveryOrderDto>>(pending);
    }

    public Task<IReadOnlyList<DeliveryOrderDto>> GetDeliveriesByDriverAsync(int driverId, CancellationToken cancellationToken = default)
    {
        var byDriver = _deliveries.Values
            .Where(d => d.DriverId == driverId)
            .OrderByDescending(d => d.CreatedAtUtc)
            .ToList();

        return Task.FromResult<IReadOnlyList<DeliveryOrderDto>>(byDriver);
    }

    public async Task AssignDriverAsync(int orderId, int driverId, CancellationToken cancellationToken = default)
    {
        if (!_deliveries.TryGetValue(orderId, out var order))
        {
            throw new KeyNotFoundException($"No se encontró la orden de entrega #{orderId}.");
        }

        if (driverId <= 0)
        {
            throw new ArgumentException("El ID de conductor es inválido.", nameof(driverId));
        }

        order.DriverId = driverId;
        if (order.Status == OrderStatus.Pending || order.Status == OrderStatus.Preparing)
        {
            order.Status = OrderStatus.OutForDelivery;
        }

        _logger?.LogInformation("[Logistics] Orden #{OrderId} asignada al conductor #{DriverId}.", orderId, driverId);

        await _mediator.Publish(new DeliveryAssignedEvent(orderId, driverId, DateTime.UtcNow), cancellationToken);
    }

    public async Task UpdateDeliveryStatusAsync(int orderId, OrderStatus newStatus, string? note = null, CancellationToken cancellationToken = default)
    {
        if (!_deliveries.TryGetValue(orderId, out var order))
        {
            throw new KeyNotFoundException($"No se encontró la orden de entrega #{orderId}.");
        }

        var previousStatus = order.Status;
        order.Status = newStatus;
        if (!string.IsNullOrWhiteSpace(note))
        {
            order.Notes = string.IsNullOrEmpty(order.Notes) ? note : $"{order.Notes} | {note}";
        }

        if (newStatus == OrderStatus.Delivered)
        {
            order.DeliveredAtUtc = DateTime.UtcNow;
        }

        _logger?.LogInformation("[Logistics] Orden #{OrderId} cambió de estado {PreviousStatus} -> {NewStatus}.",
            orderId, previousStatus, newStatus);

        await _mediator.Publish(new DeliveryStatusUpdatedEvent(orderId, previousStatus, newStatus, note, DateTime.UtcNow), cancellationToken);
    }
}
