using System;

namespace Desktop.Client.Services;

public static class ServerPortResolver
{
    public const int HttpPort = 5000;
    public const int HttpsPort = 5001;

    private static readonly char[] AuthorityTerminators = { '/', '?', '#' };

    public static int Resolve(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return HttpsPort;
        }

        var raw = address.Trim();
        bool isHttp = raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
        if (!isHttp && !raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            raw = $"https://{raw}";
        }

        int schemeSeparator = raw.IndexOf("://", StringComparison.Ordinal);
        var authority = schemeSeparator >= 0 ? raw.Substring(schemeSeparator + 3) : raw;

        int pathStart = authority.IndexOfAny(AuthorityTerminators);
        if (pathStart >= 0)
        {
            authority = authority.Substring(0, pathStart);
        }

        int userInfoEnd = authority.LastIndexOf('@');
        if (userInfoEnd >= 0)
        {
            authority = authority.Substring(userInfoEnd + 1);
        }

        int hostEnd = authority.LastIndexOf(']');
        int portSeparator = authority.IndexOf(':', hostEnd + 1);
        if (portSeparator >= 0 &&
            int.TryParse(authority.Substring(portSeparator + 1), out int explicitPort) &&
            explicitPort > 0 &&
            explicitPort <= 65535)
        {
            return explicitPort;
        }

        return isHttp ? HttpPort : HttpsPort;
    }
}
