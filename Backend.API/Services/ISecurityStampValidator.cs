using System.Threading.Tasks;

namespace Backend.API.Services;

public interface ISecurityStampValidator
{
    Task<bool> ValidateStampAsync(int userId, string tokenStamp, bool forceImmediateCheck = false);
    void InvalidateUserStamp(int userId);
    // 8.7-B1: rota el SecurityStamp en BD para revocar todos los JWT emitidos con el stamp anterior.
    Task RevokeUserStampAsync(int userId);
}