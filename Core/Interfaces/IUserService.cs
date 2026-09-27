using Core.DTOs;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces;

public interface IUserService
{
    Task<IEnumerable<UserDto>> GetUsersAsync(CancellationToken cancellationToken = default);
    Task<UserDto?> GetUserAsync(int id, CancellationToken cancellationToken = default);
    Task<string?> GetUserNameByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<UserCreatedDto> CreateUserAsync(CreateUserDto dto, CancellationToken cancellationToken = default);
    Task<(UserDto User, bool CredentialsOrRoleChanged)> UpdateUserAsync(int id, UpdateUserDto dto, int? currentUserId, CancellationToken cancellationToken = default);
    Task SoftDeleteUserAsync(int id, int? currentUserId, CancellationToken cancellationToken = default);
    Task ReactivateUserAsync(int id, CancellationToken cancellationToken = default);
    Task HardDeleteUserAsync(int id, int? currentUserId, CancellationToken cancellationToken = default);
    Task<string> UnlockUserAsync(int id, int? adminId, CancellationToken cancellationToken = default);
    Task<ResetTemporaryPasswordResponseDto> ResetTemporaryPasswordAsync(int id, int? adminId, CancellationToken cancellationToken = default);
}
