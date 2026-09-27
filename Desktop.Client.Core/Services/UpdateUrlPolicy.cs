using System;

namespace Desktop.Client.Services;

public static class UpdateUrlPolicy
{
    public static bool IsAllowed(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var parsed)) return false;
        if (parsed.Scheme == Uri.UriSchemeHttps) return true;

        return parsed.Scheme == Uri.UriSchemeHttp && parsed.IsLoopback;
    }
}
