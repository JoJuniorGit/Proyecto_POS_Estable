using System;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Core.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Backend.API.Startup;

/// <summary>
/// Utilidades de arranque del backend: resolución del certificado HTTPS y cálculo de la
/// whitelist de hosts LAN. Se extrajo del monolito Program.cs (hallazgo B11).
/// </summary>
public static class BackendHelpers
{
    /// <summary>
    /// Devuelve el certificado HTTPS si está disponible (vía Windows Certificate Store o pos-https.pfx); si no, null.
    /// Nunca usa contraseñas hardcodeadas en producción; resuelve desde Store o HTTPS_CERT_PASSWORD.
    /// </summary>
    public static X509Certificate2? LoadHttpsCertificate(IConfiguration config, IHostEnvironment env)
    {
        // 1. Prioridad: Windows Certificate Store (Recomendado en Windows Server / Entornos Corporativos)
        var thumbprint = (Environment.GetEnvironmentVariable("HTTPS_CERT_THUMBPRINT")
                          ?? config["SystemSettings:HttpsCertThumbprint"]
                          ?? config["Kestrel:Certificates:Default:Subject"])?.Replace(" ", "").ToUpperInvariant();

        if (!string.IsNullOrWhiteSpace(thumbprint))
        {
            foreach (var location in new[] { StoreLocation.LocalMachine, StoreLocation.CurrentUser })
            {
                try
                {
                    using var store = new X509Store(StoreName.My, location);
                    store.Open(OpenFlags.ReadOnly);
                    var matches = store.Certificates.Find(X509FindType.FindByThumbprint, thumbprint, validOnly: false);
                    if (matches.Count > 0)
                    {
                        AppLogger.LogStart($"[HTTPS] Certificado cargado exitosamente desde Windows Certificate Store ({location}): {matches[0].Subject}");
                        return matches[0];
                    }
                }
                catch (Exception ex)
                {
                    AppLogger.LogStart($"[HTTPS] [AVISO] Error al consultar Windows Certificate Store ({location}): {ex.Message}");
                }
            }
        }

        // 2. Archivo .pfx local en directorio certs/
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "certs", "pos-https.pfx"),
            Path.Combine(Directory.GetCurrentDirectory(), "certs", "pos-https.pfx"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                string certPassword = Environment.GetEnvironmentVariable("HTTPS_CERT_PASSWORD")
                                    ?? config["Kestrel:Certificates:Default:Password"]
                                    ?? string.Empty;

                try
                {
                    var cert = X509CertificateLoader.LoadPkcs12FromFile(candidate, certPassword);
                    AppLogger.LogStart($"[HTTPS] Certificado HTTPS cargado exitosamente desde archivo ({candidate}): {cert.Subject}");
                    return cert;
                }
                catch (Exception ex)
                {
                    AppLogger.LogStart($"[HTTPS] [AVISO] No se pudo cargar el certificado HTTPS ({candidate}): {ex.Message}");
                }
            }
        }

        // 3. (8.27-A02) Fallback dinámico SOLO en Desarrollo: en Producción un certificado
        // efímero en memoria no es válido (cambia en cada arranque y su CN/SAN son del
        // puesto de build). Se exige un certificado real configurado; sin él, Program.cs
        // aborta el arranque en Producción (fail-fast HTTPS vivo).
        if (!env.IsDevelopment())
        {
            AppLogger.LogStart("[HTTPS] [AVISO] No se usará certificado efímero en Producción; se requiere HTTPS_CERT_THUMBPRINT o certs/pos-https.pfx con HTTPS_CERT_PASSWORD.");
            return null;
        }

        // 3. Fallback dinámico (Desarrollo): Generar certificado autofirmado en memoria para asegurar disponibilidad de HTTPS
        try
        {
            using var rsa = RSA.Create(2048);
            var certRequest = new CertificateRequest(
                $"CN={Environment.MachineName}",
                rsa,
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);

            certRequest.CertificateExtensions.Add(
                new X509KeyUsageExtension(
                    X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                    critical: true));

            certRequest.CertificateExtensions.Add(
                new X509EnhancedKeyUsageExtension(
                    new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") },
                    critical: false));

            var sanBuilder = new SubjectAlternativeNameBuilder();
            sanBuilder.AddDnsName("localhost");
            sanBuilder.AddDnsName(Environment.MachineName);
            sanBuilder.AddIpAddress(IPAddress.Loopback);
            sanBuilder.AddIpAddress(IPAddress.IPv6Loopback);

            try
            {
                foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                {
                    if (ip.AddressFamily == AddressFamily.InterNetwork)
                    {
                        sanBuilder.AddIpAddress(ip);
                    }
                }
            }
            catch (Exception ex)
            {
                // 8B-B4: catch vacío convertido en aviso; se continúa con las SAN ya acumuladas.
                Console.WriteLine($"[HTTPS] Aviso: no se pudieron enumerar las IPs para el certificado autofirmado: {ex.Message}");
            }

            certRequest.CertificateExtensions.Add(sanBuilder.Build());

            var ephemeralCert = certRequest.CreateSelfSigned(
                DateTimeOffset.UtcNow.AddMinutes(-5),
                DateTimeOffset.UtcNow.AddYears(5));

            var pfxBytes = ephemeralCert.Export(X509ContentType.Pfx);
            var certWithKey = X509CertificateLoader.LoadPkcs12(pfxBytes, null);
            AppLogger.LogStart($"[HTTPS] Certificado autofirmado generado dinámicamente en memoria para {Environment.MachineName} (SANs configurados para LAN).");
            return certWithKey;
        }
        catch (Exception ex)
        {
            AppLogger.LogCrash(ex, "[HTTPS] Fallo al generar certificado autofirmado en memoria");
        }

        return null;
    }

    /// <summary>8.7-L2: devuelve la whitelist de hosts válidos para el filtro de host de ASP.NET
    /// (localhost + nombre del equipo + IPs locales), permitiendo el acceso LAN sin "*".</summary>
    public static string BuildLanAllowedHosts()
    {
        var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "localhost", "127.0.0.1", "::1", "[::1]"
        };

        try
        {
            string hostName = Dns.GetHostName();
            if (!string.IsNullOrWhiteSpace(hostName))
            {
                hosts.Add(hostName);
            }

            foreach (var ip in Dns.GetHostAddresses(hostName))
            {
                if (ip.AddressFamily == AddressFamily.InterNetwork)
                {
                    hosts.Add(ip.ToString());
                }
                else if (ip.IsIPv6LinkLocal)
                {
                    hosts.Add(ip.ToString());
                }
            }

            // 8.6-M9/L2: enumera adaptadores reales para cubrir APIPA/link-local (169.254/16 y fe80::)
            // que DNS suele omitir: en LAN sin DHCP el host del reenvío sería 169.254.x.x y la
            // whitelist anterior lo rechazaría, rompiendo el Pairing del POS.
            foreach (var networkInterface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (networkInterface.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily == AddressFamily.InterNetwork)
                    {
                        hosts.Add(address.ToString());
                    }
                    else if (address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal)
                    {
                        hosts.Add(address.ToString());
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // 8.20-M10: el catch vacío pasa a aviso; se mantiene la base localhost.
            AppLogger.LogWarn($"[AllowedHosts] No se pudo enumerar hosts DNS/adaptadores: {ex.Message}", "BackendHelpers.BuildLanAllowedHosts");
        }

        return string.Join(";", hosts);
    }
}
