using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Core.Logging;

namespace Desktop.Client.Services;

public interface ISecureTokenStorageService
{
    void SaveToken(string token);
    string? LoadToken();
    void ClearToken();
}

/// <summary>
/// Provee resguardo criptográfico para tokens JWT en el cliente de escritorio utilizando
/// Windows Data Protection API (DPAPI) con alcance al usuario actual de Windows (DataProtectionScope.CurrentUser).
/// </summary>
public class SecureTokenStorageService : ISecureTokenStorageService
{
    private static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SolucionesPOS"
    );

    private static readonly string TokenFilePath = Path.Combine(DataDirectory, "session.dat");

    // 8.16-R17: entropía por-usuario generada en primer uso (32 bytes CSPRNG) y persistida con
    // DPAPI del usuario, en lugar de una entropía estática hardcodeada en el ensamblado.
    private static readonly string EntropyFilePath = Path.Combine(DataDirectory, "entropy.dat");

    private static readonly Lazy<byte[]?> Entropy = new(LoadOrCreateEntropy);

    // Entropía legacy (static) soltada en versiones previas; se usa SOLO como fallback para
    // migrar tokens ya cifrados y re-cifrarlos con la entropía por-usuario. Nunca se usa para
    // cifrar tokens nuevos (8.16-R17).
    private static readonly byte[] LegacyEntropy = Encoding.UTF8.GetBytes("POS_Desktop_Entropy_2026_Secure");

    private static byte[]? LoadOrCreateEntropy()
    {
        if (File.Exists(EntropyFilePath))
        {
            byte[] protectedEntropy = File.ReadAllBytes(EntropyFilePath);
            return ProtectedData.Unprotect(protectedEntropy, null, DataProtectionScope.CurrentUser);
        }

        byte[] freshEntropy = RandomNumberGenerator.GetBytes(32);
        byte[] protectedFresh = ProtectedData.Protect(freshEntropy, null, DataProtectionScope.CurrentUser);

        string? directory = Path.GetDirectoryName(EntropyFilePath);
        if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllBytes(EntropyFilePath, protectedFresh);
        return freshEntropy;
    }

    public void SaveToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            ClearToken();
            return;
        }

        try
        {
            byte[]? entropy = Entropy.Value;
            if (entropy == null)
            {
                ClientStateLogger.LogWarning("[SECURE_STORAGE] Entropía por-usuario no disponible; el token NO se persistirá (fail-safe, sin entropía estática).", nameof(SecureTokenStorageService));
                ClearToken();
                return;
            }

            string? directory = Path.GetDirectoryName(TokenFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            byte[] plaintextBytes = Encoding.UTF8.GetBytes(token);
            byte[] encryptedBytes = ProtectedData.Protect(plaintextBytes, entropy, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(TokenFilePath, encryptedBytes);
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogWarning($"[SECURE_STORAGE] Error al resguardar token con DPAPI: {ex.Message}", nameof(SecureTokenStorageService));
        }
    }

    public string? LoadToken()
    {
        try
        {
            if (!File.Exists(TokenFilePath))
            {
                return null;
            }

            byte[] encryptedBytes = File.ReadAllBytes(TokenFilePath);

            byte[]? newEntropy = Entropy.Value;

            // Primero se intenta con la entropía por-usuario.
            try
            {
                if (newEntropy == null)
                {
                    throw new CryptographicException("Entropía por-usuario no disponible.");
                }

                byte[] decryptedBytes = ProtectedData.Unprotect(encryptedBytes, newEntropy, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(decryptedBytes);
            }
            catch (CryptographicException)
            {
                // La entropía por-usuario no descifra: puede ser un token legacy. Se intenta con la
                // legacy y, si funciona, se re-cifra con la nueva entropía (migración).
                try
                {
                    byte[] legacyDecrypted = ProtectedData.Unprotect(encryptedBytes, LegacyEntropy, DataProtectionScope.CurrentUser);
                    string token = Encoding.UTF8.GetString(legacyDecrypted);
                    SaveToken(token);
                    return token;
                }
                catch (CryptographicException)
                {
                    throw;
                }
            }
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogWarning($"[SECURE_STORAGE] Error al descifrar token con DPAPI: {ex.Message}", nameof(SecureTokenStorageService));
            ClearToken();
            return null;
        }
    }

    public void ClearToken()
    {
        try
        {
            if (File.Exists(TokenFilePath))
            {
                File.Delete(TokenFilePath);
            }
        }
        catch (Exception ex)
        {
            ClientStateLogger.LogWarning($"[SECURE_STORAGE] Error al eliminar token resguardado: {ex.Message}", nameof(SecureTokenStorageService));
        }
    }
}
