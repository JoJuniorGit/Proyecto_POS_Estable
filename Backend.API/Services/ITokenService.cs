using Core.Entities;

namespace Backend.API.Services;

public interface ITokenService
{
    string GenerateToken(User user);
}
