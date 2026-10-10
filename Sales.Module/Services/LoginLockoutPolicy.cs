using Core.Entities;

namespace Sales.Module.Services;

/// <summary>
/// 8.150 (T2, design D2): politica de lockout del login extraida de AuthService para que el
/// fallback local de autorizaciones reutilice exactamente las mismas reglas (5 intentos,
/// 15 minutos) sin inventar una politica paralela. La mutacion es en memoria; el llamador
/// persiste via SaveChangesAsync.
/// </summary>
public static class LoginLockoutPolicy
{
    public const int MaxFailedAttempts = 5;
    public const int LockoutMinutes = 15;

    public static bool IsLockedOut(User user, DateTime utcNow)
        => user.LockoutEndUtc.HasValue && user.LockoutEndUtc.Value > utcNow;

    public static void ClearExpiredLockout(User user, DateTime utcNow)
    {
        if (user.LockoutEndUtc.HasValue && user.LockoutEndUtc.Value <= utcNow)
        {
            user.AccessFailedCount = 0;
            user.LockoutEndUtc = null;
        }
    }

    public static bool RegisterFailedAttempt(User user, DateTime utcNow)
    {
        user.AccessFailedCount++;
        if (user.AccessFailedCount >= MaxFailedAttempts && !IsLockedOut(user, utcNow))
        {
            user.LockoutEndUtc = utcNow.AddMinutes(LockoutMinutes);
            return true;
        }

        return false;
    }

    public static void ResetOnSuccess(User user)
    {
        if (user.AccessFailedCount > 0 || user.LockoutEndUtc.HasValue)
        {
            user.AccessFailedCount = 0;
            user.LockoutEndUtc = null;
        }
    }
}
