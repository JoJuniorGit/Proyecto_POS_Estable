using System.Threading.Tasks;

namespace Backend.API.Services;

public interface ISecurityStampValidator
{
    Task<bool> ValidateStampAsync(int userId, string tokenStamp, bool forceImmediateCheck = false);
    void InvalidateUserStamp(int userId);
    // 8.7-B1: rota el SecurityStamp en BD para revocar todos los JWT emitidos con el stamp anterior.
    Task RevokeUserStampAsync(int userId);
    // 8.157 (SEC-07): invalida la caché del sello y expulsa las conexiones SignalR del usuario
    // (grupos user:{id} de ambos hubs) con un push fail-soft.
    Task InvalidateUserSessionsAsync(int userId);
}