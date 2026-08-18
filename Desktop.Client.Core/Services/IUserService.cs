using System.Collections.Generic;
using System.Threading.Tasks;
using Core.DTOs;

namespace Desktop.Client.Services;

public interface IUserService
{
    Task<LoginResultDto?> LoginAsync(string cedula);
    Task<bool> ChangePasswordAsync(string cedula, string currentPassword, string newPassword);
    Task<List<UserDto>> GetUsersAsync();
    Task<UserDto?> CreateUserAsync(CreateUserDto dto);
    Task<UserDto?> UpdateUserAsync(int id, UpdateUserDto dto);
    Task<bool> SoftDeleteUserAsync(int id);
    Task<bool> ReactivateUserAsync(int id);
    Task<bool> PermanentDeleteUserAsync(int id);
}
