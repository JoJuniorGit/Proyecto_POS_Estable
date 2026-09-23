using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Text.Json;
using Core.Logging;

namespace Desktop.Client.Services;

public interface ICertificatePinStore
{
    IReadOnlyDictionary<string, string> Load();
    void Save(IReadOnlyDictionary<string, string> pins);
}

public sealed class DpapiCertificatePinStore : ICertificatePinStore
{
    public static readonly string DefaultFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SolucionesPOS",
        "certificate-pins.dat"
    );

    private readonly string _filePath;

    public DpapiCertificatePinStore(string? filePath = null)
    {
        _filePath = filePath ?? DefaultFilePath;
    }

    public IReadOnlyDictionary<string, string> Load()
    {
        var pins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            if (!File.Exists(_filePath))
            {
                return pins;
            }

            byte[] protectedBytes = File.ReadAllBytes(_filePath);
            byte[] plainBytes = ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            var stored = JsonSerializer.Deserialize<Dictionary<string, string>>(plainBytes);

            if (stored != null)
            {
                foreach (var pair in stored)
                {
                    pins[pair.Key] = pair.Value;
                }
            }
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogWarning($"[SECURITY] Error al cargar pins TLS persistidos; TOFU de primer contacto activo solo para hosts no fijados: {ex.Message}", nameof(DpapiCertificatePinStore));
        }

        return pins;
    }

    public void Save(IReadOnlyDictionary<string, string> pins)
    {
        try
        {
            string? directory = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            byte[] plainBytes = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string>(pins, StringComparer.OrdinalIgnoreCase));
            byte[] protectedBytes = ProtectedData.Protect(plainBytes, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(_filePath, protectedBytes);
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogWarning($"[SECURITY] Error al persistir pins TLS con DPAPI; el pin queda activo solo en memoria: {ex.Message}", nameof(DpapiCertificatePinStore));
        }
    }
}

public sealed class CertificatePinValidator
{
    private readonly ConcurrentDictionary<string, string> _knownThumbprints = new(StringComparer.OrdinalIgnoreCase);
    private readonly ICertificatePinStore _store;
    private readonly object _sync = new();

    public CertificatePinValidator(ICertificatePinStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;

        foreach (var pair in store.Load())
        {
            _knownThumbprints.TryAdd(pair.Key, pair.Value);
        }
    }

    public bool IsTrusted(string host, X509Certificate? cert, SslPolicyErrors errors)
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

        if (_knownThumbprints.TryGetValue(host, out var pinned))
        {
            return string.Equals(pinned, thumbprint, StringComparison.OrdinalIgnoreCase);
        }

        lock (_sync)
        {
            if (_knownThumbprints.TryGetValue(host, out pinned))
            {
                return string.Equals(pinned, thumbprint, StringComparison.OrdinalIgnoreCase);
            }

            _knownThumbprints[host] = thumbprint;
            _store.Save(new Dictionary<string, string>(_knownThumbprints, StringComparer.OrdinalIgnoreCase));
            return true;
        }
    }
}

/// <summary>
/// 8.16-R18 / 8.9-M4: pinning TOFU (Trust-On-First-Use) compartido de certificados para
/// hosts privados/locales. El primer thumbprint visto por host queda registrado de forma
/// durable (DPAPI CurrentUser) y cualquier certificado distinto posterior es rechazado,
/// mitigando MITM en la LAN. Se comparte entre el escáner de subred y el servicio de tasa
/// de cambio para un mismo repositorio de pins.
/// </summary>
public static class CertificatePinning
{
    private static readonly CertificatePinValidator Validator = new(new DpapiCertificatePinStore());

    public static bool IsTrusted(string host, X509Certificate? cert, SslPolicyErrors errors)
    {
        return Validator.IsTrusted(host, cert, errors);
    }
}
