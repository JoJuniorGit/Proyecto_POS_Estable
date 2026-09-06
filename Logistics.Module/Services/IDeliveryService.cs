using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.Entities;
using Logistics.Module.DTOs;

namespace Logistics.Module.Services;

public interface IDeliveryService
{
    Task<DeliveryOrderDto> RegisterDeliveryOrderAsync(DeliveryOrderDto order, CancellationToken cancellationToken = default);
    Task<DeliveryOrderDto?> GetDeliveryOrderAsync(int orderId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeliveryOrderDto>> GetPendingDeliveriesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeliveryOrderDto>> GetDeliveriesByDriverAsync(int driverId, CancellationToken cancellationToken = default);
    Task AssignDriverAsync(int orderId, int driverId, CancellationToken cancellationToken = default);
    Task UpdateDeliveryStatusAsync(int orderId, OrderStatus newStatus, string? note = null, CancellationToken cancellationToken = default);
}
