using System;

namespace Core.Common;

/// <summary>
/// 8.211-T3c1 (F3c1): resolves legacy local-currency aliases to the canonical ISO code. The
/// aliases are fixed in code and never exposed as a mutable set.
/// </summary>
public static class CurrencyCodeNormalizer
{
    private const string VesCode = "VES";

    /// <summary>
    /// Trims and uppercases <paramref name="code"/> through <see cref="Iso4217.Normalize"/>, then
    /// returns its canonical ISO 4217 code. <c>Bs.S</c> and <c>BsS</c> resolve to <c>VES</c>.
    /// Returns <see langword="null"/> for missing, malformed, or unsupported codes.
    /// </summary>
    public static string? NormalizeToIso(string? code)
    {
        var normalized = Iso4217.Normalize(code);
        if (normalized is null)
        {
            return null;
        }

        if (normalized is "BS.S" or "BSS")
        {
            return VesCode;
        }

        return Iso4217.IsSupported(normalized) ? normalized : null;
    }

    /// <summary>Indicates whether <paramref name="code"/> resolves to the local ISO code.</summary>
    public static bool IsLocal(string? code) => NormalizeToIso(code) == VesCode;
}
