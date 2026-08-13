using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading.Tasks;
using Core.DTOs;

namespace Desktop.Client.Services;

public class UserService : IUserService
{
    private readonly HttpClient _httpClient;

    public UserService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<UserDto?> LoginAsync(string cedula)
    {
        var response = await _httpClient.PostAsJsonAsync("api/auth/login", new LoginRequest { Cedula = cedula });
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            string msg = err != null && err.ContainsKey("message") ? err["message"] : "Error al iniciar sesión.";
            throw new System.Exception(msg);
        }
        return await response.Content.ReadFromJsonAsync<UserDto>();
    }

    public async Task<List<UserDto>> GetUsersAsync()
    {
        var response = await _httpClient.GetAsync("api/users");
        if (!response.IsSuccessStatusCode) return new List<UserDto>();
        return await response.Content.ReadFromJsonAsync<List<UserDto>>() ?? new List<UserDto>();
    }

    public async Task<UserDto?> CreateUserAsync(CreateUserDto dto)
    {
        var response = await _httpClient.PostAsJsonAsync("api/users", dto);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            string msg = err != null && err.ContainsKey("message") ? err["message"] : "Error al crear usuario.";
            throw new System.Exception(msg);
        }
        return await response.Content.ReadFromJsonAsync<UserDto>();
    }

    public async Task<UserDto?> UpdateUserAsync(int id, UpdateUserDto dto)
    {
        var response = await _httpClient.PutAsJsonAsync($"api/users/{id}", dto);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            string msg = err != null && err.ContainsKey("message") ? err["message"] : "Error al actualizar usuario.";
            throw new System.Exception(msg);
        }
        return await response.Content.ReadFromJsonAsync<UserDto>();
    }

    public async Task<bool> SoftDeleteUserAsync(int id)
    {
        var response = await _httpClient.DeleteAsync($"api/users/{id}");
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            string msg = err != null && err.ContainsKey("message") ? err["message"] : "Error al desactivar usuario.";
            throw new System.Exception(msg);
        }
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> ReactivateUserAsync(int id)
    {
        var response = await _httpClient.PostAsync($"api/users/{id}/reactivate", null);
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            string msg = err != null && err.ContainsKey("message") ? err["message"] : "Error al reactivar usuario.";
            throw new System.Exception(msg);
        }
        return response.IsSuccessStatusCode;
    }

    public async Task<bool> PermanentDeleteUserAsync(int id)
    {
        var response = await _httpClient.DeleteAsync($"api/users/{id}/permanent");
        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
            string msg = err != null && err.ContainsKey("message") ? err["message"] : "Error al eliminar usuario permanentemente.";
            throw new System.Exception(msg);
        }
        return response.IsSuccessStatusCode;
    }
}
