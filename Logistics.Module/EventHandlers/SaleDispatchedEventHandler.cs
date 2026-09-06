using System;
using System.Threading;
using System.Threading.Tasks;
using Core.Entities;
using Core.Events;
using Logistics.Module.DTOs;
using Logistics.Module.Services;
using MediatR;
using Microsoft.Extensions.Logging;

namespace Logistics.Module.EventHandlers;

/// <summary>
/// Handles domain events when a sale is dispatched, cleanly decoupling Sales.Module from Logistics.Module.
/// </summary>
public class SaleDispatchedEventHandler : INotificationHandler<SaleDispatchedEvent>
{
    private readonly IDeliveryService _deliveryService;
    private readonly ILogger<SaleDispatchedEventHandler>? _logger;

    public SaleDispatchedEventHandler(IDeliveryService deliveryService, ILogger<SaleDispatchedEventHandler>? logger = null)
    {
        _deliveryService = deliveryService ?? throw new ArgumentNullException(nameof(deliveryService));
        _logger = logger;
    }

    public async Task Handle(SaleDispatchedEvent notification, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(notification.DeliveryAddress))
        {
            // Venta de mostrador sin entrega a domicilio; no requiere logística de delivery.
            return;
        }

        var deliveryOrder = new DeliveryOrderDto
        {
            OrderId = notification.SaleId,
            OrderNumber = notification.InvoiceNumber,
            CustomerName = notification.CustomerName,
            CustomerPhone = notification.CustomerPhone,
            DeliveryAddress = notification.DeliveryAddress,
            TotalAmount = notification.TotalAmount,
            Status = OrderStatus.Preparing,
            CreatedAtUtc = notification.DispatchedAtUtc
        };

        await _deliveryService.RegisterDeliveryOrderAsync(deliveryOrder, cancellationToken);
        _logger?.LogInformation("[Logistics] Evento SaleDispatchedEvent procesado: creado pedido de entrega #{SaleId} para '{Address}'.",
            notification.SaleId, notification.DeliveryAddress);
    }
}
