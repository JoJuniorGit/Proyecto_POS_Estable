using Sales.Module.Entities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Sales.Module.Services;

public static class ClosurePdfGenerator
{
    public static byte[] GeneratePdf(DailyClosure closure, bool isBlind = false)
    {
        var ms = new MemoryStream();
        var writer = new StreamWriter(ms, Encoding.ASCII);

        string dateStr = closure.ClosureDate.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss");
        string userName = string.IsNullOrWhiteSpace(closure.UserId) ? "Cajero Activo" : closure.UserId;
        string userCedula = string.IsNullOrWhiteSpace(closure.Observation) || closure.Observation.StartsWith("Test") ? "V-00000000" : closure.Observation;

        var contentSb = new StringBuilder();

        // 1. Top Header Box (Dark Indigo Background)
        contentSb.AppendLine("0.12 0.11 0.29 rg"); // Dark indigo fill
        contentSb.AppendLine("40 700 532 55 re f");

        // Fixed title with standard hyphen (never renders '?')
        WriteTextLeft(contentSb, isBlind ? "COMPROBANTE DE ARQUEO A CIEGAS" : "REPORTE Z - CIERRE DE CAJA", 55, 732, "/F2 16 Tf", "1 1 1 rg");
        WriteTextLeft(contentSb, isBlind ? "Resumen Oficial de Montos Declarados por Cajero" : "Comprobante Oficial de Arqueo y Descuadre de Caja", 55, 712, "/F1 9 Tf", "0.65 0.71 0.99 rg");

        // 2. Metadata Card Box
        contentSb.AppendLine("0.97 0.98 0.99 rg"); // Slate background fill
        contentSb.AppendLine("40 625 532 60 re f");
        contentSb.AppendLine("0.8 0.84 0.88 RG 1 w"); // Border
        contentSb.AppendLine("40 625 532 60 re S");

        string generalStatus = Math.Abs(closure.TotalDifferenceBsS) < 0.05m ? "Cuadrado" : (closure.TotalDifferenceBsS > 0 ? "Sobrante" : "Faltante");

        WriteTextLeft(contentSb, $"N° de Turno: #Z-{closure.Id}", 52, 667, "/F2 9 Tf", "0.2 0.25 0.33 rg");
        WriteTextLeft(contentSb, $"Cajero: {userName}", 52, 653, "/F2 9 Tf", "0.2 0.25 0.33 rg");
        WriteTextLeft(contentSb, $"Cédula: {userCedula}", 52, 639, "/F2 9 Tf", "0.2 0.25 0.33 rg");

        WriteTextLeft(contentSb, $"Fecha: {dateStr}", 360, 667, "/F2 9 Tf", "0.2 0.25 0.33 rg");
        WriteTextLeft(contentSb, $"Estado: {(isBlind ? "Declarado" : generalStatus)}", 360, 653, "/F2 9 Tf", "0.2 0.25 0.33 rg");

        // 3. Table Headers (Strict Accounting Alignment)
        int yPos = 590;
        contentSb.AppendLine("0.2 0.25 0.33 rg"); // Dark header fill
        contentSb.AppendLine($"40 {yPos} 532 22 re f");

        WriteTextLeft(contentSb, "MÉTODO DE PAGO", 48, yPos + 7, "/F2 8.5 Tf", "1 1 1 rg");
        WriteTextCenter(contentSb, "MONEDA", 185, yPos + 7, "/F2 8.5 Tf", "1 1 1 rg");
        WriteTextRight(contentSb, "MONTO DECLARADO (Bs.S)", 315, yPos + 7, "/F2 8.5 Tf", "1 1 1 rg", true);

        if (!isBlind)
        {
            WriteTextRight(contentSb, "MONTO SISTEMA (Bs.S)", 440, yPos + 7, "/F2 8.5 Tf", "1 1 1 rg", true);
            WriteTextRight(contentSb, "DIFERENCIA (Bs.S)", 565, yPos + 7, "/F2 8.5 Tf", "1 1 1 rg", true);
        }

        // 4. Table Rows
        yPos -= 22;
        int rowIdx = 0;
        foreach (var detail in closure.Details)
        {
            if (yPos < 140) break; // Page boundary safeguard

            if (rowIdx % 2 == 1)
            {
                contentSb.AppendLine($"0.95 0.96 0.97 rg 40 {yPos} 532 20 re f");
            }

            string currency = detail.PaymentMethodName.Contains("USD", StringComparison.OrdinalIgnoreCase) ? "USD" : "Bs.S";
            string declaredValStr = detail.ActualAmountBsS.ToString("N2");
            string systemValStr = isBlind ? "-" : detail.ExpectedAmountBsS.ToString("N2");
            
            string diffValStr;
            string diffColor;

            if (isBlind)
            {
                diffValStr = "-";
                diffColor = "0.1 0.1 0.1 rg";
            }
            else
            {
                decimal diff = detail.DifferenceBsS;
                if (Math.Abs(diff) < 0.05m)
                {
                    diffValStr = "0,00";
                    diffColor = "0.09 0.64 0.29 rg"; // Green for Cuadrado
                }
                else if (diff > 0)
                {
                    diffValStr = $"+ {diff:N2} (Sobrante)";
                    diffColor = "0.15 0.39 0.92 rg"; // Blue for Sobrante
                }
                else
                {
                    diffValStr = $"- {Math.Abs(diff):N2} (Faltante)";
                    diffColor = "0.86 0.15 0.15 rg"; // Dark Red for Faltante
                }
            }

            WriteTextLeft(contentSb, detail.PaymentMethodName, 48, yPos + 6, "/F1 8.5 Tf", "0.1 0.1 0.1 rg");
            WriteTextCenter(contentSb, currency, 185, yPos + 6, "/F2 8.5 Tf", "0.2 0.25 0.33 rg");
            WriteTextRight(contentSb, declaredValStr, 315, yPos + 6, "/F1 8.5 Tf", "0.1 0.1 0.1 rg");

            if (!isBlind)
            {
                WriteTextRight(contentSb, systemValStr, 440, yPos + 6, "/F1 8.5 Tf", "0.3 0.3 0.3 rg");
                WriteTextRight(contentSb, diffValStr, 565, yPos + 6, "/F2 8.5 Tf", diffColor, true);
            }

            // Row Bottom Line
            contentSb.AppendLine($"0.88 0.9 0.92 RG 0.5 w 40 {yPos} 532 0 m 572 {yPos} l S");

            yPos -= 20;
            rowIdx++;
        }

        // 5. Table Totals Row (Accounting Rule)
        contentSb.AppendLine($"0.86 0.9 0.94 rg 40 {yPos - 2} 532 22 re f");
        contentSb.AppendLine($"0.7 0.75 0.8 RG 1 w 40 {yPos - 2} 532 22 re S");

        string totalDiffStr;
        string totalDiffColor;
        if (isBlind)
        {
            totalDiffStr = "-";
            totalDiffColor = "0.1 0.15 0.25 rg";
        }
        else
        {
            decimal totalDiff = closure.TotalDifferenceBsS;
            if (Math.Abs(totalDiff) < 0.05m)
            {
                totalDiffStr = "0,00";
                totalDiffColor = "0.09 0.64 0.29 rg"; // Green
            }
            else if (totalDiff > 0)
            {
                totalDiffStr = $"+ {totalDiff:N2} (Sobrante)";
                totalDiffColor = "0.15 0.39 0.92 rg"; // Blue
            }
            else
            {
                totalDiffStr = $"- {Math.Abs(totalDiff):N2} (Faltante)";
                totalDiffColor = "0.86 0.15 0.15 rg"; // Red
            }
        }

        WriteTextLeft(contentSb, "TOTALES", 48, yPos + 4, "/F2 9 Tf", "0.1 0.15 0.25 rg");
        WriteTextCenter(contentSb, "-", 185, yPos + 4, "/F2 9 Tf", "0.1 0.15 0.25 rg");
        WriteTextRight(contentSb, closure.TotalActualBsS.ToString("N2"), 315, yPos + 4, "/F2 9 Tf", "0.1 0.15 0.25 rg", true);

        if (!isBlind)
        {
            WriteTextRight(contentSb, closure.TotalExpectedBsS.ToString("N2"), 440, yPos + 4, "/F2 9 Tf", "0.1 0.15 0.25 rg", true);
            WriteTextRight(contentSb, totalDiffStr, 565, yPos + 4, "/F2 9 Tf", totalDiffColor, true);
        }

        yPos -= 36;

        // 6. Structured Account Statement Box (Clean Executive Right-Aligned Statement)
        contentSb.AppendLine($"0.96 0.97 0.98 rg 40 {yPos - 60} 532 60 re f");
        contentSb.AppendLine($"0.8 0.84 0.88 RG 1 w 40 {yPos - 60} 532 60 re S");

        WriteTextRight(contentSb, $"TOTAL DECLARADO:   Bs.S {closure.TotalActualBsS:N2}", 555, yPos - 18, "/F2 9.5 Tf", "0.15 0.2 0.3 rg", true);
        if (!isBlind)
        {
            WriteTextRight(contentSb, $"TOTAL ESPERADO:    Bs.S {closure.TotalExpectedBsS:N2}", 555, yPos - 32, "/F2 9.5 Tf", "0.15 0.2 0.3 rg", true);
            WriteTextRight(contentSb, $"DIFERENCIA TOTAL:  Bs.S {totalDiffStr}", 555, yPos - 46, "/F2 9.5 Tf", totalDiffColor, true);
        }

        // 7. Footer Signature Box
        WriteTextLeft(contentSb, "Sistema de Administración y Punto de Venta — Comprobante Generado Automáticamente", 40, 35, "/F1 8 Tf", "0.5 0.5 0.5 rg");
        WriteTextRight(contentSb, $"Fecha Impr: {dateStr}", 572, 35, "/F1 8 Tf", "0.5 0.5 0.5 rg");

        string streamText = contentSb.ToString();
        byte[] streamBytes = Encoding.ASCII.GetBytes(streamText);

        // Build PDF Structure
        var objects = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R /F2 5 0 R >> >> /Contents 6 0 R >>\nendobj",
            "4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj",
            $"6 0 obj\n<< /Length {streamBytes.Length} >>\nstream\n{streamText}\nendstream\nendobj"
        };

        // Calculate byte offsets for XRef table
        writer.WriteLine("%PDF-1.4");
        writer.Flush();
        long offset = ms.Position;

        var offsets = new List<long>();
        foreach (var obj in objects)
        {
            offsets.Add(offset);
            byte[] objBytes = Encoding.ASCII.GetBytes(obj + "\n");
            ms.Write(objBytes, 0, objBytes.Length);
            offset = ms.Position;
        }

        long startXRef = offset;
        writer.WriteLine("xref");
        writer.WriteLine($"0 {objects.Count + 1}");
        writer.WriteLine("0000000000 65535 f ");
        foreach (var off in offsets)
        {
            writer.WriteLine($"{off:D10} 00000 n ");
        }

        writer.WriteLine("trailer");
        writer.WriteLine($"<< /Size {objects.Count + 1} /Root 1 0 R >>");
        writer.WriteLine("startxref");
        writer.WriteLine(startXRef);
        writer.WriteLine("%%EOF");
        writer.Flush();

        return ms.ToArray();
    }

    private static void WriteTextLeft(StringBuilder sb, string text, float x, float y, string font, string color)
    {
        sb.AppendLine("BT");
        sb.AppendLine($"{font}");
        sb.AppendLine($"{color}");
        sb.AppendLine($"{x:0.##} {y:0.##} Td");
        sb.AppendLine($"({PdfEscape(text)}) Tj");
        sb.AppendLine("ET");
    }

    private static void WriteTextCenter(StringBuilder sb, string text, float centerX, float y, string font, string color, bool isBold = false)
    {
        float fontSize = GetFontSizeFromFont(font);
        float width = MeasureTextWidth(text, fontSize, isBold);
        float leftX = centerX - (width / 2f);

        sb.AppendLine("BT");
        sb.AppendLine($"{font}");
        sb.AppendLine($"{color}");
        sb.AppendLine($"{leftX:0.##} {y:0.##} Td");
        sb.AppendLine($"({PdfEscape(text)}) Tj");
        sb.AppendLine("ET");
    }

    private static void WriteTextRight(StringBuilder sb, string text, float rightX, float y, string font, string color, bool isBold = false)
    {
        float fontSize = GetFontSizeFromFont(font);
        float width = MeasureTextWidth(text, fontSize, isBold);
        float leftX = rightX - width;

        sb.AppendLine("BT");
        sb.AppendLine($"{font}");
        sb.AppendLine($"{color}");
        sb.AppendLine($"{leftX:0.##} {y:0.##} Td");
        sb.AppendLine($"({PdfEscape(text)}) Tj");
        sb.AppendLine("ET");
    }

    private static float GetFontSizeFromFont(string font)
    {
        if (font.Contains("16 Tf")) return 16f;
        if (font.Contains("10 Tf")) return 10f;
        if (font.Contains("9.5 Tf")) return 9.5f;
        if (font.Contains("9 Tf")) return 9f;
        if (font.Contains("8.5 Tf")) return 8.5f;
        if (font.Contains("8 Tf")) return 8f;
        return 9f;
    }

    private static float MeasureTextWidth(string text, float fontSize, bool isBold)
    {
        if (string.IsNullOrEmpty(text)) return 0f;
        float total = 0f;
        float scale = fontSize / 1000f;

        foreach (char c in text)
        {
            float w;
            if (c >= '0' && c <= '9') w = 556f;
            else switch (c)
            {
                case ' ': w = 278f; break;
                case '.': case ',': case ':': case ';': w = 278f; break;
                case '-': case '+': w = 556f; break;
                case '(': case ')': w = 333f; break;
                case 'M': case 'W': w = 833f; break;
                case 'I': case 'i': case 'l': case 't': case '1': w = 278f; break;
                default:
                    if (char.IsUpper(c)) w = 667f;
                    else w = 500f;
                    break;
            }
            total += w * scale;
        }
        return total;
    }

    private static string PdfEscape(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var sb = new StringBuilder();
        foreach (char c in text)
        {
            switch (c)
            {
                case '(': sb.Append(@"\("); break;
                case ')': sb.Append(@"\)"); break;
                case '\\': sb.Append(@"\\"); break;
                case 'á': sb.Append(@"\341"); break;
                case 'é': sb.Append(@"\351"); break;
                case 'í': sb.Append(@"\355"); break;
                case 'ó': sb.Append(@"\363"); break;
                case 'ú': sb.Append(@"\372"); break;
                case 'ñ': sb.Append(@"\361"); break;
                case 'Ñ': sb.Append(@"\321"); break;
                case 'Á': sb.Append(@"\301"); break;
                case 'É': sb.Append(@"\311"); break;
                case 'Í': sb.Append(@"\315"); break;
                case 'Ó': sb.Append(@"\323"); break;
                case 'Ú': sb.Append(@"\332"); break;
                case '°': sb.Append(@"\260"); break;
                default:
                    if (c < 128) sb.Append(c);
                    else sb.Append('?');
                    break;
            }
        }
        return sb.ToString();
    }
}
