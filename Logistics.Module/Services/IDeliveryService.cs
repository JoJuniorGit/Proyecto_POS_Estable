using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Core.Entities;
using Logistics.Module.DTOs;

namespace Logistics.Module.Services;

/// <summary>
/// [NO PRODUCTIVO / EXPERIMENTAL] Servicio de gestión de entregas en memoria.
/// No apto para entornos de producción hasta completar la persistencia EF Core en PostgreSQL ([8L-CR3]).
/// </summary>
public interface IDeliveryService
{
    /// <summary>
    /// Indica si el servicio cuenta con persistencia y es apto para producción (actualmente false).
    /// </summary>
    bool IsProductionReady => false;

    Task<DeliveryOrderDto> RegisterDeliveryOrderAsync(DeliveryOrderDto order, CancellationToken cancellationToken = default);
    Task<DeliveryOrderDto?> GetDeliveryOrderAsync(int orderId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeliveryOrderDto>> GetPendingDeliveriesAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<DeliveryOrderDto>> GetDeliveriesByDriverAsync(int driverId, CancellationToken cancellationToken = default);
    Task AssignDriverAsync(int orderId, int driverId, CancellationToken cancellationToken = default);
    Task UpdateDeliveryStatusAsync(int orderId, OrderStatus newStatus, string? note = null, CancellationToken cancellationToken = default);
}
