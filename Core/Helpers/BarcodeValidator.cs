using System;
using System.Text.RegularExpressions;

namespace Core.Helpers;

/// <summary>
/// Validador centralizado para lecturas de códigos de barras.
/// Restringe las lecturas exclusivamente a códigos de barras estándar de productos (1D),
/// aplicando validación matemática de dígito verificador (GS1 Módulo 10) para formatos comerciales
/// estándar (EAN-13, UPC-A, EAN-8 y códigos de balanza 20-29), y admitiendo códigos alfanuméricos
/// internos (Code-128, Code-39, SKUs cortos de 4 a 15 caracteres) sin caracteres especiales ni URLs.
/// </summary>
public static class BarcodeValidator
{
    /// <summary>
    /// Expresión regular compilada: solo caracteres alfanuméricos sin espacios, de 4 a 15 caracteres.
    /// Cubre formatos estándar EAN-13, EAN-8, UPC-A, UPC-E, Code-128 y SKUs cortos internos.
    /// </summary>
    private static readonly Regex StandardBarcodeRegex = new(
        @"^[A-Za-z0-9]{4,15}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Valida el dígito verificador matemático según el algoritmo estándar GS1 Módulo 10.
    /// Aplica ponderación alternada de 3 y 1 desde el penúltimo dígito hacia la izquierda.
    /// Compatible con:
    /// - EAN-13 (13 dígitos, incluyendo prefijos de balanza GS1 20 a 29).
    /// - UPC-A (12 dígitos).
    /// - EAN-8 (8 dígitos).
    /// </summary>
    /// <param name="digits">Cadena o span que contiene únicamente dígitos numéricos '0'-'9'.</param>
    /// <returns>True si el último dígito coincide con el dígito verificador calculado; false en caso contrario.</returns>
    public static bool ValidateGs1Mod10Checksum(ReadOnlySpan<char> digits)
    {
        if (digits.Length is not (8 or 12 or 13))
        {
            return false;
        }

        int checkDigit = digits[^1] - '0';
        if (checkDigit < 0 || checkDigit > 9)
        {
            return false;
        }

        int sum = 0;
        int weight = 3;

        // Ponderación de derecha a izquierda: 3, 1, 3, 1, ...
        for (int i = digits.Length - 2; i >= 0; i--)
        {
            int d = digits[i] - '0';
            if (d < 0 || d > 9)
            {
                return false;
            }
            sum += d * weight;
            weight = weight == 3 ? 1 : 3;
        }

        int calculatedCheck = (10 - (sum % 10)) % 10;
        return calculatedCheck == checkDigit;
    }

    /// <summary>
    /// Determina si una lectura corresponde a un código de barras de producto válido.
    /// 
    /// NOTA SOBRE CÓDIGOS DE BALANZA (Prefijos GS1 20 a 29):
    /// En entornos minoristas, los códigos de balanza para productos de peso variable siguen la
    /// estructura '20AAAAA CCCCC K' (13 dígitos con checksum Mod10 estándar calculado por la balanza).
    /// En esta fase, BarcodeValidator verifica la integridad del formato y checksum para no rechazarlos
    /// erróneamente. La descomposición del payload de peso/importe se gestiona en la capa de venta.
    /// </summary>
    /// <param name="code">Cadena capturada por el lector óptico, pistola USB o teclado.</param>
    /// <returns>True si es un código de barras válido de 4 a 15 caracteres; false si es QR, URL, corrupto o inválido.</returns>
    public static bool IsValidBarcode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var trimmed = code.Trim();

        // Filtro rápido de longitud mínima y máxima (4 a 15 caracteres)
        if (trimmed.Length < 4 || trimmed.Length > 15)
        {
            return false;
        }

        // Filtro estricto de caracteres y marcadores típicos de QR / URLs
        if (trimmed.Contains("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains('?') ||
            trimmed.Contains('=') ||
            trimmed.Contains('&') ||
            trimmed.Contains('/') ||
            trimmed.Contains('\\') ||
            trimmed.Contains(':') ||
            trimmed.Contains('#') ||
            trimmed.Contains(' ') ||
            trimmed.Contains('\t') ||
            trimmed.Contains('\r') ||
            trimmed.Contains('\n'))
        {
            return false;
        }

        // Validación de formato alfanumérico base
        if (!StandardBarcodeRegex.IsMatch(trimmed))
        {
            return false;
        }

        // Si es puramente numérico y coincide con una longitud GS1 estándar (13, 12 u 8 dígitos),
        // se valida matemáticamente el dígito verificador Mod10 para descartar lecturas truncadas o corruptas.
        if (trimmed.Length is 8 or 12 or 13 && IsAllDigits(trimmed))
        {
            return ValidateGs1Mod10Checksum(trimmed);
        }

        // Para códigos alfanuméricos y SKUs internos de otras longitudes (ej. 4-7, 9-11, 14-15),
        // se acepta como código 1D válido.
        return true;
    }

    private static bool IsAllDigits(string str)
    {
        for (int i = 0; i < str.Length; i++)
        {
            if (str[i] < '0' || str[i] > '9')
            {
                return false;
            }
        }
        return true;
    }
}
