using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Core.DTOs;
using Core.Entities;
using Core.Logging;

namespace Desktop.Client.Services;

public partial class UserSession : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoggedIn))]
    [NotifyPropertyChangedFor(nameof(IsAdmin))]
    [NotifyPropertyChangedFor(nameof(IsManager))]
    [NotifyPropertyChangedFor(nameof(IsCashier))]
    [NotifyPropertyChangedFor(nameof(CanMutateCatalog))]
    [NotifyPropertyChangedFor(nameof(CanMutateSettings))]
    [NotifyPropertyChangedFor(nameof(CanMutateExchangeRate))]
    [NotifyPropertyChangedFor(nameof(UserName))]
    [NotifyPropertyChangedFor(nameof(UserRoleDisplay))]
    private UserDto? _currentUser;

    [ObservableProperty]
    private string? _token;

    private readonly ISecureTokenStorageService? _secureTokenStorage;

    public UserSession() : this(null)
    {
    }

    public UserSession(ISecureTokenStorageService? secureTokenStorage)
    {
        _secureTokenStorage = secureTokenStorage;
    }

    public bool IsLoggedIn => CurrentUser != null;
    public bool IsAdmin => CurrentUser != null && CurrentUser.Role == UserRole.Admin;
    public bool IsManager => CurrentUser != null && CurrentUser.Role == UserRole.Manager;
    public bool IsCashier => CurrentUser != null && CurrentUser.Role == UserRole.Cashier;

    public bool CanMutateCatalog => CurrentUser != null && (CurrentUser.Role == UserRole.Admin || CurrentUser.Role == UserRole.Manager);
    public bool CanMutateSettings => CurrentUser != null && CurrentUser.Role == UserRole.Admin;
    public bool CanMutateExchangeRate => CurrentUser != null && CurrentUser.Role == UserRole.Admin;

    public string UserName => CurrentUser != null ? CurrentUser.Name : "Sin sesión";
    public string UserRoleDisplay => CurrentUser != null ? (CurrentUser.Role switch
    {
        UserRole.Admin => "Administrador",
        UserRole.Manager => "Gerente",
        UserRole.Driver => "Conductor",
        _ => "Cajero"
    }) : "";

    public event Action? SessionChanged;

    public void SetUser(UserDto user, string? token = null)
    {
        CurrentUser = user;
        Token = token;
        if (!string.IsNullOrWhiteSpace(token))
        {
            _secureTokenStorage?.SaveToken(token);
        }
        SessionChanged?.Invoke();
    }

    public void Logout()
    {
        CurrentUser = null;
        Token = null;
        _secureTokenStorage?.ClearToken();
        SessionChanged?.Invoke();
    }

    /// <summary>
    /// Intenta restaurar la sesión del usuario a partir del token JWT resguardado mediante DPAPI.
    /// Valida que el token no esté expirado, que posea el scope pos:desktop y que contenga los claims de identidad válidos.
    /// </summary>
    /// <returns>True si la sesión fue restaurada con éxito; false en caso contrario.</returns>
    public bool TryRestoreTokenFromStorage()
    {
        if (_secureTokenStorage == null) return false;

        try
        {
            var savedToken = _secureTokenStorage.LoadToken();
            if (string.IsNullOrWhiteSpace(savedToken))
            {
                return false;
            }

            var parts = savedToken.Split('.');
            if (parts.Length != 3)
            {
                ClientStateLogger.LogWarning("[SESSION] Token resguardado con formato inválido. Purgando.", nameof(UserSession));
                _secureTokenStorage.ClearToken();
                return false;
            }

            var payloadJson = Base64UrlDecode(parts[1]);
            using var doc = System.Text.Json.JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;

            // Verificar expiración (exp en segundos UNIX)
            if (root.TryGetProperty("exp", out var expProp) && expProp.TryGetInt64(out var expUnix))
            {
                var expiry = DateTimeOffset.FromUnixTimeSeconds(expUnix);
                if (expiry <= DateTimeOffset.UtcNow)
                {
                    ClientStateLogger.LogWarning($"[SESSION] Token resguardado expirado en {expiry:u}. Purgando.", nameof(UserSession));
                    _secureTokenStorage.ClearToken();
                    return false;
                }
            }

            // Verificar scope de cliente de escritorio
            var scope = GetClaimValue(root, "scope");
            if (!string.Equals(scope, "pos:desktop", StringComparison.OrdinalIgnoreCase))
            {
                ClientStateLogger.LogWarning($"[SESSION] Token resguardado tiene scope incompatible ({scope}). Purgando.", nameof(UserSession));
                _secureTokenStorage.ClearToken();
                return false;
            }

            // Verificar presencia de security_stamp para compatibilidad con la política de revocación y auditoría
            var stamp = GetClaimValue(root, "security_stamp");
            if (string.IsNullOrWhiteSpace(stamp))
            {
                ClientStateLogger.LogWarning("[SESSION] Token resguardado no posee 'security_stamp'. Purgando sesión obsoleta.", nameof(UserSession));
                _secureTokenStorage.ClearToken();
                return false;
            }

            // Extraer ID de usuario
            var idStr = GetClaimValue(root, "nameid", "sub", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/nameidentifier", "id");
            if (!int.TryParse(idStr, out var userId) || userId <= 0)
            {
                ClientStateLogger.LogWarning("[SESSION] No se pudo determinar el ID de usuario desde los claims del token. Purgando.", nameof(UserSession));
                _secureTokenStorage.ClearToken();
                return false;
            }

            // Extraer nombre
            var name = GetClaimValue(root, "name", "unique_name", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name") ?? "Usuario";

            // Extraer rol
            var roleStr = GetClaimValue(root, "role", "http://schemas.microsoft.com/ws/2008/06/identity/claims/role");
            if (!Enum.TryParse<UserRole>(roleStr, true, out var userRole))
            {
                userRole = UserRole.Cashier;
            }

            // Extraer cédula
            var cedula = GetClaimValue(root, "serialnumber", "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/serialnumber", "cedula") ?? string.Empty;

            var userDto = new UserDto
            {
                Id = userId,
                Name = name,
                Role = userRole,
                Cedula = cedula,
                IsActive = true
            };

            CurrentUser = userDto;
            Token = savedToken;
            ClientStateLogger.LogInfo($"[SESSION] Sesión restaurada con éxito para usuario '{name}' ({userRole}).", nameof(UserSession));
            SessionChanged?.Invoke();
            return true;
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogWarning($"[SESSION] Error al restaurar sesión desde almacenamiento seguro: {ex.Message}. Purgando.", nameof(UserSession));
            try { _secureTokenStorage?.ClearToken(); } catch { }
            return false;
        }
    }

    private static string? GetClaimValue(System.Text.Json.JsonElement root, params string[] claimNames)
    {
        foreach (var name in claimNames)
        {
            if (root.TryGetProperty(name, out var prop))
            {
                var val = prop.GetString();
                if (!string.IsNullOrEmpty(val)) return val;
            }
        }
        return null;
    }

    private static string Base64UrlDecode(string input)
    {
        string output = input.Replace('-', '+').Replace('_', '/');
        switch (output.Length % 4)
        {
            case 0: break;
            case 2: output += "=="; break;
            case 3: output += "="; break;
            default: throw new FormatException("Illegal base64url string!");
        }
        var bytes = Convert.FromBase64String(output);
        return System.Text.Encoding.UTF8.GetString(bytes);
    }
}
