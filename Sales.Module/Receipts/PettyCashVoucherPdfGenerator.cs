using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Core.Helpers;
using Sales.Module.Entities;

namespace Sales.Module.Receipts;

/// <summary>
/// T3 (Paso 13 / 8.188, S7): generador PDF del vale de caja chica. Sigue el estilo y la
/// plomeria de <see cref="CreditPaymentReceiptPdfGenerator"/> (PDF 1.4 minimo, fuentes Type1,
/// xref manual) con contenido propio: correlativo VCC-000001, fecha/hora de Venezuela,
/// turno, categoria, concepto, beneficiario, referencia, monto nominal de la divisa extraida,
/// equivalente bimonetario con la tasa congelada, cajero/autorizador, sello de anulacion (con la
/// linea del supervisor que aprobo la anulacion cuando exista, H3 del cierre 8.188) y
/// espacios de firma "Entregado por" / "Recibido conforme por".
/// </summary>
public static class PettyCashVoucherPdfGenerator
{
    public static byte[] BuildPdf(PettyCashVoucherContext context, MoneyDisplayFormat format)
    {
        ArgumentNullException.ThrowIfNull(context);

        var content = BuildContent(context, format);
        byte[] contentBytes = Encoding.ASCII.GetBytes(content);
        var objects = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj",
            "2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj",
            "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R /F2 5 0 R >> >> /Contents 6 0 R >>\nendobj",
            "4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj",
            "5 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj",
            $"6 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n{content}\nendstream\nendobj"
        };

        using var ms = new MemoryStream();
        using (var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            writer.WriteLine("%PDF-1.4");
            writer.Flush();
        }

        var offsets = new List<long>();
        foreach (var obj in objects)
        {
            offsets.Add(ms.Position);
            byte[] objBytes = Encoding.ASCII.GetBytes(obj + "\n");
            ms.Write(objBytes, 0, objBytes.Length);
        }

        long startXRef = ms.Position;
        using (var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
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
        }

        return ms.ToArray();
    }

    private static string BuildContent(PettyCashVoucherContext context, MoneyDisplayFormat format)
    {
        var sb = new StringBuilder();
        sb.AppendLine("0.12 0.11 0.29 rg");
        sb.AppendLine("40 700 532 55 re f");
        WriteLeft(sb, "VALE DE CAJA CHICA", 55, 730, "/F2 15 Tf", "1 1 1 rg");
        WriteLeft(sb, "Comprobante de egreso operativo - No fiscal", 55, 712, "/F1 9 Tf", "0.65 0.71 0.99 rg");

        sb.AppendLine("0.97 0.98 0.99 rg");
        sb.AppendLine("40 612 532 80 re f");
        sb.AppendLine("0.8 0.84 0.88 RG 1 w");
        sb.AppendLine("40 612 532 80 re S");

        WriteLeft(sb, $"Vale Nro: {context.VoucherCode}", 52, 674, "/F2 9 Tf", "0.2 0.25 0.33 rg");
        WriteLeft(sb, $"Fecha: {context.CreatedAtVenezuela.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}", 52, 660, "/F1 9 Tf", "0.2 0.25 0.33 rg");
        WriteLeft(sb, $"Turno/Sesion: {BuildSessionLabel(context)}", 52, 646, "/F1 9 Tf", "0.2 0.25 0.33 rg");
        WriteLeft(sb, $"Moneda: {context.CurrencyCode}", 52, 632, "/F1 9 Tf", "0.2 0.25 0.33 rg");
        WriteLeft(sb, $"Categoria: {context.CategoryLabel}", 360, 674, "/F2 9 Tf", "0.2 0.25 0.33 rg");
        WriteLeft(sb, $"Beneficiario: {Truncate(context.Beneficiary, 28)}", 360, 660, "/F1 9 Tf", "0.2 0.25 0.33 rg");
        WriteLeft(sb, $"Referencia: {Truncate(context.ReferenceNumber, 28)}", 360, 646, "/F1 9 Tf", "0.2 0.25 0.33 rg");
        WriteLeft(sb, $"Tasa aplicada: {MoneyFormat.Number(context.HistoricalRate, format, 4)}", 360, 632, "/F1 9 Tf", "0.2 0.25 0.33 rg");

        sb.AppendLine("0.2 0.25 0.33 rg");
        sb.AppendLine("40 578 532 22 re f");
        WriteLeft(sb, "DETALLE DEL GASTO", 48, 585, "/F2 8.5 Tf", "1 1 1 rg");

        WriteLeft(sb, "Concepto:", 52, 560, "/F2 9 Tf", "0.2 0.25 0.33 rg");
        int conceptY = 546;
        foreach (var line in WrapText(context.Concept, 96, 4))
        {
            WriteLeft(sb, line, 52, conceptY, "/F1 9 Tf", "0.1 0.1 0.1 rg");
            conceptY -= 13;
        }

        // Monto entregado: nominal de la divisa fisica extraida con el peso del documento.
        sb.AppendLine("0.12 0.11 0.29 rg");
        sb.AppendLine("40 470 532 26 re f");
        WriteLeft(sb, $"MONTO ENTREGADO ({context.CurrencySymbol})", 48, 479, "/F2 10 Tf", "1 1 1 rg");
        WriteRight(sb, FormatNominal(context, format), 565, 478, "/F2 12 Tf", "1 1 1 rg", true);

        string equivalent = context.IncludesUsd
            ? $"Equivalente: {context.LocalCurrencySymbol} {MoneyFormat.Number(context.AmountBsS, format, 2)} / {context.UsdCurrencySymbol} {MoneyFormat.Number(context.AmountUsd, format, 4)}"
            : $"Equivalente: {context.LocalCurrencySymbol} {MoneyFormat.Number(context.AmountBsS, format, 2)}";
        WriteRight(sb, equivalent, 565, 450, "/F1 9 Tf", "0.15 0.2 0.3 rg", true);
        WriteRight(sb, $"Tasa aplicada: {MoneyFormat.Number(context.HistoricalRate, format, 4)}", 565, 436, "/F1 8.5 Tf", "0.3 0.3 0.3 rg", true);

        WriteLeft(sb, $"Cajero: {Truncate(context.CashierName, 60)}", 52, 410, "/F2 9 Tf", "0.2 0.25 0.33 rg");
        WriteLeft(sb, $"Autorizado por: {Truncate(context.AuthorizerName, 60)}", 52, 396, "/F1 9 Tf", "0.2 0.25 0.33 rg");

        if (context.IsCancelled)
        {
            // S5: la anulacion es logica; el documento impreso debe mostrar su marca. H3 (cierre
            // 8.188): cuando hubo supervisor aprobador distinto del ejecutor se agrega su linea y
            // la banda crece una linea; sin el, el render queda igual que antes.
            bool hasCancellationAuthorizer = !string.IsNullOrWhiteSpace(context.CancellationAuthorizedName);
            sb.AppendLine("0.72 0.11 0.11 rg");
            sb.AppendLine(hasCancellationAuthorizer ? "40 316 532 62 re f" : "40 330 532 48 re f");
            WriteLeft(sb, "DOCUMENTO ANULADO", 55, 364, "/F2 12 Tf", "1 1 1 rg");
            string cancelledBy = context.CancelledByName is null ? PettyCashVoucherContext.UserNotFound : context.CancelledByName;
            WriteLeft(sb, $"Anulado el {context.CancelledAtVenezuela?.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture) ?? "-"} por {cancelledBy}", 55, 348, "/F1 8.5 Tf", "1 0.88 0.88 rg");
            WriteLeft(sb, $"Motivo: {Truncate(context.CancellationReason ?? "-", 96)}", 55, 336, "/F1 8.5 Tf", "1 0.88 0.88 rg");
            if (hasCancellationAuthorizer)
            {
                WriteLeft(sb, $"Autorizó anulación: {Truncate(context.CancellationAuthorizedName!, 96)}", 55, 324, "/F1 8.5 Tf", "1 0.88 0.88 rg");
            }
        }

        int signatureY = context.IsCancelled ? 250 : 320;
        WriteLeft(sb, $"Entregado por: {Truncate(context.CashierName, 40)}", 40, signatureY, "/F1 9 Tf", "0.2 0.25 0.33 rg");
        sb.AppendLine($"0.35 0.35 0.35 RG 0.8 w 40 {signatureY - 22} 240 0 m 280 {signatureY - 22} l S");
        WriteLeft(sb, "Firma del cajero", 40, signatureY - 36, "/F1 8 Tf", "0.45 0.45 0.45 rg");

        WriteLeft(sb, $"Recibido conforme por: {Truncate(context.Beneficiary, 34)}", 320, signatureY, "/F1 9 Tf", "0.2 0.25 0.33 rg");
        sb.AppendLine($"0.35 0.35 0.35 RG 0.8 w 320 {signatureY - 22} 240 0 m 560 {signatureY - 22} l S");
        WriteLeft(sb, "Firma del beneficiario", 320, signatureY - 36, "/F1 8 Tf", "0.45 0.45 0.45 rg");

        WriteLeft(sb, "Sistema de Administracion y Punto de Venta - Comprobante no fiscal", 40, 35, "/F1 8 Tf", "0.5 0.5 0.5 rg");
        return sb.ToString();
    }

    private static string BuildSessionLabel(PettyCashVoucherContext context)
    {
        if (!context.SessionOpenedAtVenezuela.HasValue)
        {
            return $"#{context.SessionId}";
        }

        return $"#{context.SessionId} - Apertura {context.SessionOpenedAtVenezuela.Value.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}";
    }

    private static string FormatNominal(PettyCashVoucherContext context, MoneyDisplayFormat format)
        => context.Currency == PettyCashCurrency.Usd
            ? $"{context.CurrencySymbol} {MoneyFormat.Number(context.NominalAmount, format, 4)}"
            : $"{context.CurrencySymbol} {MoneyFormat.Number(context.NominalAmount, format, 2)}";

    /// <summary>
    /// Envuelve el texto en lineas de ancho fijo respetando palabras (hasta <paramref name="maxLines"/>;
    /// el desborde se marca con "..."), para que el concepto detallado (hasta 500 chars) no se corte
    /// en una sola linea del vale.
    /// </summary>
    private static IReadOnlyList<string> WrapText(string? text, int maxCharsPerLine, int maxLines)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return lines;
        }

        string normalized = text.Trim();
        int index = 0;
        while (index < normalized.Length && lines.Count < maxLines)
        {
            int length = Math.Min(maxCharsPerLine, normalized.Length - index);
            if (index + length < normalized.Length)
            {
                int lastSpace = normalized.LastIndexOf(' ', index + length - 1, length);
                if (lastSpace > index)
                {
                    length = lastSpace - index;
                }
            }

            lines.Add(normalized.Substring(index, length).Trim());
            index += length;
            while (index < normalized.Length && normalized[index] == ' ')
            {
                index++;
            }
        }

        if (index < normalized.Length && lines.Count > 0)
        {
            string last = lines[^1];
            lines[^1] = last.Length + 3 <= maxCharsPerLine ? last + "..." : last[..Math.Max(1, maxCharsPerLine - 3)] + "...";
        }

        return lines;
    }

    private static void WriteLeft(StringBuilder sb, string text, float x, float y, string font, string color)
    {
        sb.AppendLine("BT");
        sb.AppendLine($"{font}");
        sb.AppendLine($"{color}");
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1:0.##} Td", x, y));
        sb.AppendLine($"({Escape(text)}) Tj");
        sb.AppendLine("ET");
    }

    private static void WriteRight(StringBuilder sb, string text, float rightX, float y, string font, string color, bool bold = false)
    {
        float width = Measure(text, font, bold);
        float left = rightX - width;
        sb.AppendLine("BT");
        sb.AppendLine($"{font}");
        sb.AppendLine($"{color}");
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1:0.##} Td", left, y));
        sb.AppendLine($"({Escape(text)}) Tj");
        sb.AppendLine("ET");
    }

    private static string Truncate(string? value, int max)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= max) return value ?? string.Empty;
        return value[..max];
    }

    private static float Measure(string text, string font, bool bold)
    {
        float size = font switch
        {
            var f when f.Contains("15 Tf") => 15f,
            var f when f.Contains("12 Tf") => 12f,
            var f when f.Contains("10 Tf") => 10f,
            var f when f.Contains("9.5 Tf") => 9.5f,
            var f when f.Contains("9 Tf") => 9f,
            var f when f.Contains("8.5 Tf") => 8.5f,
            _ => 8f
        };
        float scale = size / 1000f;
        float total = 0f;
        if (string.IsNullOrEmpty(text)) return total;
        foreach (char c in text)
        {
            if (c >= '0' && c <= '9') total += 556f * scale;
            else if (c == ' ') total += 278f * scale;
            else if (c == '.' || c == ',' || c == ':') total += 278f * scale;
            else if (char.IsUpper(c)) total += 667f * scale;
            else total += 500f * scale;
        }
        return total;
    }

    private static string Escape(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
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
                // Quick win 8.188: signos y grado del espanol (CP1252) que caian a '?'.
                case '¿': sb.Append(@"\277"); break;
                case '¡': sb.Append(@"\241"); break;
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
