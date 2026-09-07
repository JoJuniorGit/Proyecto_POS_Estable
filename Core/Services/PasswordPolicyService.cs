using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Core.Interfaces;

namespace Core.Services;

public class PasswordPolicyService : IPasswordPolicyService
{
    private static readonly HashSet<string> DefaultBlacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "admin123", "admin123!", "administrator", "administrador", "password", "password123",
        "123456", "12345678", "123456789", "1234567890", "posadmin", "clave123", "sistema",
        "sistema123", "cajero", "cajero123", "gerente", "gerente123", "supervisor",
        "qwerty123", "letmein123", "welcome123", "admin2026", "pos2026", "postgres"
    };

    private readonly HashSet<string> _blacklist;

    // Alfabeto sin caracteres ambiguos (excluye 0, O, o, 1, l, I, 8, B)
    private const string UnambiguousUpper = "ACDEFGHJKLMNPQRSTUVWXYZ"; // sin B, I, O
    private const string UnambiguousLower = "abcdefghijkmnpqrstuvwxyz";  // sin l, o
    private const string UnambiguousDigits = "2345679";                 // sin 0, 1, 8
    private const string UnambiguousSpecial = "!@#$%*-_+=?";

    public PasswordPolicyService(IEnumerable<string>? customBlacklist = null)
    {
        _blacklist = new HashSet<string>(DefaultBlacklist, StringComparer.OrdinalIgnoreCase);
        if (customBlacklist != null)
        {
            foreach (var item in customBlacklist)
            {
                if (!string.IsNullOrWhiteSpace(item))
                {
                    _blacklist.Add(item.Trim());
                }
            }
        }
    }

    public (bool IsValid, string? ErrorMessage) ValidatePassword(string password, string? username = null)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return (false, "La contraseña no puede estar vacía.");
        }

        if (password.Length < 8)
        {
            return (false, "La contraseña debe tener al menos 8 caracteres.");
        }

        if (password.Length > 128)
        {
            return (false, "La contraseña no puede exceder los 128 caracteres.");
        }

        // Validación inmediata frente a lista negra común
        if (_blacklist.Contains(password.Trim()))
        {
            return (false, "La contraseña ingresada es demasiado común o predecible. Elija una contraseña más segura.");
        }

        if (!password.Any(char.IsUpper))
        {
            return (false, "La contraseña debe incluir al menos una letra mayúscula.");
        }

        if (!password.Any(char.IsLower))
        {
            return (false, "La contraseña debe incluir al menos una letra minúscula.");
        }

        if (!password.Any(char.IsDigit))
        {
            return (false, "La contraseña debe incluir al menos un número o dígito.");
        }

        if (!password.Any(ch => !char.IsLetterOrDigit(ch)))
        {
            return (false, "La contraseña debe incluir al menos un carácter especial (!@#$%^&*...).");
        }

        // Validación frente a username / cédula
        if (!string.IsNullOrWhiteSpace(username))
        {
            var cleanUser = username.Trim().ToLowerInvariant();
            var cleanPass = password.ToLowerInvariant();

            if (cleanPass.Contains(cleanUser))
            {
                return (false, "La contraseña no puede contener su nombre de usuario o cédula.");
            }

            var digitsOnly = Regex.Replace(cleanUser, @"[^\d]", "");
            if (digitsOnly.Length >= 4 && cleanPass.Contains(digitsOnly))
            {
                return (false, "La contraseña no puede contener los dígitos de su cédula.");
            }
        }

        return (true, null);
    }

    public string GenerateSecureTemporaryPassword(int length = 12)
    {
        length = Math.Clamp(length, 12, 32);

        var chars = new List<char>();

        // Garantizar al menos 2 de cada clase requerida
        for (int i = 0; i < 2; i++)
        {
            chars.Add(GetRandomChar(UnambiguousUpper));
            chars.Add(GetRandomChar(UnambiguousLower));
            chars.Add(GetRandomChar(UnambiguousDigits));
            chars.Add(GetRandomChar(UnambiguousSpecial));
        }

        // Rellenar los caracteres restantes desde el pool combinado
        string combined = UnambiguousUpper + UnambiguousLower + UnambiguousDigits + UnambiguousSpecial;
        while (chars.Count < length)
        {
            chars.Add(GetRandomChar(combined));
        }

        // Mezclar con Fisher-Yates criptográfico
        for (int i = chars.Count - 1; i > 0; i--)
        {
            int j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        var result = new string(chars.ToArray());

        // Asegurar que la contraseña generada apruebe la política
        var (isValid, _) = ValidatePassword(result);
        if (!isValid)
        {
            // Fallback recursivo seguro
            return GenerateSecureTemporaryPassword(length);
        }

        return result;
    }

    private static char GetRandomChar(string pool)
    {
        int idx = RandomNumberGenerator.GetInt32(pool.Length);
        return pool[idx];
    }
}
