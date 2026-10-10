using System.Threading;
using System.Threading.Tasks;
using Sales.Module.DTOs;

namespace Sales.Module.Interfaces;

/// <summary>
/// 8.150 (T3, design D4): eventos del hub de autorizaciones que la capa de dominio publica sin
/// conocer SignalR. La implementacion SignalRAuthorizationNotifier viaja con T4.
/// </summary>
public interface IAuthorizationNotifier
{
    Task NotifyRequestCreatedAsync(AuthorizationRequestDto request, CancellationToken cancellationToken = default);

    Task NotifyResolvedAsync(AuthorizationRequestDto request, string? token, CancellationToken cancellationToken = default);

    Task NotifyExpiredAsync(AuthorizationRequestDto request, CancellationToken cancellationToken = default);
}
