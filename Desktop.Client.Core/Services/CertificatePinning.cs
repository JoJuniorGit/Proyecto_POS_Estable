using System;
using System.Collections.Concurrent;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;

namespace Desktop.Client.Services;

/// <summary>
/// 8.16-R18 / 8.9-M4: pinning TOFU (Trust-On-First-Use) compartido de certificados para
/// hosts privados/locales. El primer thumbprint visto por host queda registrado y cualquier
/// certificado distinto posterior es rechazado, mitigando MITM en la LAN. Se comparte entre
/// el escáner de subred y el servicio de tasa de cambio para un mismo repositorio de pins.
/// </summary>
public static class CertificatePinning
{
    private static readonly ConcurrentDictionary<string, string> KnownThumbprints = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsTrusted(string host, X509Certificate? cert, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        if (!SubnetScannerService.IsPrivateOrLocalAddress(host))
        {
            return false;
        }

        var thumbprint = cert?.GetCertHashString() ?? string.Empty;
        if (string.IsNullOrEmpty(thumbprint))
        {
            return false;
        }

        var pinned = KnownThumbprints.AddOrUpdate(
            host,
            thumbprint,
            (_, existing) => existing);

        return string.Equals(pinned, thumbprint, StringComparison.OrdinalIgnoreCase);
    }
}
