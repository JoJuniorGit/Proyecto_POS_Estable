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
    private static readonly string TokenFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SolucionesPOS",
        "session.dat"
    );

    private static readonly byte[] OptionalEntropy = Encoding.UTF8.GetBytes("POS_Desktop_Entropy_2026_Secure");

    public void SaveToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            ClearToken();
            return;
        }

        try
        {
            var directory = Path.GetDirectoryName(TokenFilePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            byte[] plaintextBytes = Encoding.UTF8.GetBytes(token);
            byte[] encryptedBytes = ProtectedData.Protect(plaintextBytes, OptionalEntropy, DataProtectionScope.CurrentUser);
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
            byte[] decryptedBytes = ProtectedData.Unprotect(encryptedBytes, OptionalEntropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decryptedBytes);
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
