using System;
using System.Text;

namespace Sales.Module.Receipts;

/// <summary>
/// 8.211-T6-U14: PDF text uses the built-in Helvetica/WinAnsi encoding. Keep Latin-1 accents
/// and the common WinAnsi punctuation/currency glyphs as octal bytes instead of collapsing every
/// non-ASCII character to '?'. Characters outside WinAnsi still fall back to '?'.
/// </summary>
public static class PdfTextEscape
{
    public static string EncodeChar(char value)
    {
        if (value < 128)
        {
            return value.ToString();
        }

        if (TryGetWinAnsiByte(value, out var encoded))
        {
            return "\\" + Convert.ToString(encoded, 8).PadLeft(3, '0');
        }

        return "?";
    }

    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        foreach (var character in text)
        {
            switch (character)
            {
                case '(':
                    builder.Append(@"\(");
                    break;
                case ')':
                    builder.Append(@"\)");
                    break;
                case '\\':
                    builder.Append(@"\\");
                    break;
                default:
                    builder.Append(EncodeChar(character));
                    break;
            }
        }

        return builder.ToString();
    }

    private static bool TryGetWinAnsiByte(char value, out byte encoded)
    {
        if (value is >= '\u00A0' and <= '\u00FF')
        {
            encoded = (byte)value;
            return true;
        }

        encoded = value switch
        {
            '\u20AC' => 0x80,
            '\u201A' => 0x82,
            '\u0192' => 0x83,
            '\u201E' => 0x84,
            '\u2026' => 0x85,
            '\u2020' => 0x86,
            '\u2021' => 0x87,
            '\u02C6' => 0x88,
            '\u2030' => 0x89,
            '\u0160' => 0x8A,
            '\u2039' => 0x8B,
            '\u0152' => 0x8C,
            '\u017D' => 0x8E,
            '\u2018' => 0x91,
            '\u2019' => 0x92,
            '\u201C' => 0x93,
            '\u201D' => 0x94,
            '\u2022' => 0x95,
            '\u2013' => 0x96,
            '\u2014' => 0x97,
            '\u02DC' => 0x98,
            '\u2122' => 0x99,
            '\u0161' => 0x9A,
            '\u203A' => 0x9B,
            '\u0153' => 0x9C,
            '\u017E' => 0x9E,
            '\u0178' => 0x9F,
            _ => 0
        };

        return encoded != 0;
    }
}
