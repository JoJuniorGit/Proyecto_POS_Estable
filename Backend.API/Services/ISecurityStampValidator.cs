using System.Threading.Tasks;

namespace Backend.API.Services;

public interface ISecurityStampValidator
{
    Task<bool> ValidateStampAsync(int userId, string tokenStamp, bool forceImmediateCheck = false);
    void InvalidateUserStamp(int userId);
}
