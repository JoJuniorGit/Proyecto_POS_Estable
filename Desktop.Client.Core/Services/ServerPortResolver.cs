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

    public static string NormalizeHttpsAddress(string? address)
    {
        var raw = string.IsNullOrWhiteSpace(address) ? string.Empty : address.Trim();
        if (raw.Length == 0)
        {
            return $"https://localhost:{HttpsPort}/";
        }

        if (!raw.Contains("://", StringComparison.Ordinal))
        {
            raw = $"https://{raw}";
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri)
            || (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                && !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return $"https://localhost:{HttpsPort}/";
        }

        int httpsPort;
        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            // Sin puerto explícito, el POS escucha HTTPS en el puerto dedicado, no en el
            // estándar 443 (salvo que el usuario haya escrito 443 explícitamente).
            httpsPort = uri.IsDefaultPort ? HttpsPort : uri.Port;
        }
        else if (uri.IsDefaultPort || uri.Port == HttpPort)
        {
            httpsPort = HttpsPort;
        }
        else
        {
            httpsPort = uri.Port < 65535 ? uri.Port + 1 : HttpsPort;
        }

        var secureUri = new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeHttps,
            Port = httpsPort
        };
        if (string.IsNullOrEmpty(secureUri.Path))
        {
            secureUri.Path = "/";
        }
        else if (!secureUri.Path.EndsWith("/", StringComparison.Ordinal))
        {
            secureUri.Path += "/";
        }

        return secureUri.Uri.ToString();
    }
}
