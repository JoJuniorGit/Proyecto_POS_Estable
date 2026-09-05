using System;
using System.Linq;

namespace Core.Constants;

public static class SecurityConstants
{
    public static readonly string[] RootAdminCedulas = { "V-00000000", "V-12345678" };
    public const string RootAdminUsername = "Admin";

    public static bool IsRootAdmin(string? cedula, string? username = null)
    {
        if (!string.IsNullOrWhiteSpace(cedula) && RootAdminCedulas.Any(c => c.Equals(cedula.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(username) && RootAdminUsername.Equals(username.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
