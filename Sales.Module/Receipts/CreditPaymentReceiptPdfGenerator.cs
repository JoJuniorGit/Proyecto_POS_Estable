using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Core.Helpers;

namespace Sales.Module.Receipts;

/// <summary>
/// T3a (F2b, S5): generador PDF del comprobante de abono CxC. Sigue el estilo y la plomería del
/// <see cref="SaleReceiptPdfGenerator"/> (PDF 1.4 mínimo, fuentes Type1, xref manual) pero con
/// contenido propio: saldo anterior/nuevo por factura, monto abonado en USD y Bs.S a la tasa del
/// abono, saldo pendiente del cliente en USD con equivalente Bs.S del día y tasa del pago.
/// Se mantiene autocontenido para no tocar el generador de ventas existente.
/// </summary>
public static class CreditPaymentReceiptPdfGenerator
{
    public static byte[] BuildPdf(CreditPaymentReceiptContext context, MoneyDisplayFormat format)
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

    private static string BuildContent(CreditPaymentReceiptContext context, MoneyDisplayFormat format)
    {
        var sb = new StringBuilder();
        sb.AppendLine("0.12 0.11 0.29 rg");
        sb.AppendLine("40 700 532 55 re f");
        WriteLeft(sb, "COMPROBANTE DE ABONO", 55, 730, "/F2 16 Tf", "1 1 1 rg");
        WriteLeft(sb, "Credito CxC - Comprobante de recaudacion - No fiscal", 55, 712, "/F1 9 Tf", "0.65 0.71 0.99 rg");

        sb.AppendLine("0.97 0.98 0.99 rg");
        sb.AppendLine("40 608 532 84 re f");
        sb.AppendLine("0.8 0.84 0.88 RG 1 w");
        sb.AppendLine("40 608 532 84 re S");

        WriteLeft(sb, $"Cliente: {context.CustomerName ?? "Consumidor Final"}", 52, 675, "/F1 9 Tf", "0.2 0.25 0.33 rg");
        WriteLeft(sb, $"Cedula: {context.CustomerCedula ?? "V-00000000"}", 52, 661, "/F1 9 Tf", "0.2 0.25 0.33 rg");
        WriteLeft(sb, $"Metodo: {context.PaymentMethodName ?? "N/D"}", 52, 647, "/F1 9 Tf", "0.2 0.25 0.33 rg");
        WriteLeft(sb, $"Referencia: {context.ReferenceNumber ?? "-"}", 52, 633, "/F1 9 Tf", "0.2 0.25 0.33 rg");
        WriteLeft(sb, $"Fecha: {context.ReceivedAtUtc.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}", 360, 675, "/F1 9 Tf", "0.2 0.25 0.33 rg");
        WriteLeft(sb, $"Comprobante Nro: {context.PaymentId:D6}", 360, 661, "/F1 9 Tf", "0.2 0.25 0.33 rg");
        WriteLeft(sb, $"Tasa del abono: {MoneyFormat.Number(context.PaymentRate, format, 4)}", 360, 647, "/F1 9 Tf", "0.2 0.25 0.33 rg");

        int y = 578;
        sb.AppendLine("0.2 0.25 0.33 rg");
        sb.AppendLine($"40 {y} 532 22 re f");
        WriteLeft(sb, "FACTURA", 48, y + 7, "/F2 8.5 Tf", "1 1 1 rg");
        WriteRight(sb, "SALDO ANTERIOR", 250, y + 7, "/F2 8.5 Tf", "1 1 1 rg");
        WriteRight(sb, $"ABONO {context.PrimaryCurrencyCode}", 350, y + 7, "/F2 8.5 Tf", "1 1 1 rg");
        if (!context.IsSingleCurrency)
        {
            WriteRight(sb, $"ABONO {context.SecondaryCurrencyCode}", 460, y + 7, "/F2 8.5 Tf", "1 1 1 rg");
        }
        WriteRight(sb, "NUEVO SALDO", 565, y + 7, "/F2 8.5 Tf", "1 1 1 rg");

        y -= 22;
        foreach (var line in context.Lines)
        {
            if (y < 170) break;
            sb.AppendLine($"0.88 0.9 0.92 RG 0.5 w 40 {y} 532 0 m 572 {y} l S");
            string invoice = line.InvoiceNumber.HasValue ? $"#{line.InvoiceNumber.Value:D5}" : $"CxC-{line.ReceivableId}";
            WriteLeft(sb, invoice, 48, y + 6, "/F1 8.5 Tf", "0.1 0.1 0.1 rg");
            WriteRight(sb, MoneyFormat.Number(line.BalanceBeforeUSD, format), 250, y + 6, "/F1 8.5 Tf", "0.1 0.1 0.1 rg");
            WriteRight(sb, MoneyFormat.Number(line.AmountUSD, format), 350, y + 6, "/F1 8.5 Tf", "0.1 0.1 0.1 rg");
            if (!context.IsSingleCurrency)
            {
                WriteRight(sb, MoneyFormat.Number(line.AmountBsS, format), 460, y + 6, "/F1 8.5 Tf", "0.1 0.1 0.1 rg");
            }
            WriteRight(sb, MoneyFormat.Number(line.BalanceAfterUSD, format), 565, y + 6, "/F1 8.5 Tf", "0.1 0.1 0.1 rg");
            y -= 20;
        }

        y -= 12;
        sb.AppendLine($"0.86 0.9 0.94 rg 40 {y - 2} 532 22 re f");
        sb.AppendLine($"0.7 0.75 0.8 RG 1 w 40 {y - 2} 532 22 re S");
        WriteLeft(sb, "TOTAL ABONADO", 48, y + 4, "/F2 9 Tf", "0.1 0.15 0.25 rg");
        WriteRight(sb, context.IsSingleCurrency
            ? $"{context.PrimaryCurrencySymbol} {MoneyFormat.Number(context.TotalAmountUSD, format)}"
            : $"{context.PrimaryCurrencySymbol} {MoneyFormat.Number(context.TotalAmountUSD, format)} / {context.SecondaryCurrencySymbol} {MoneyFormat.Number(context.TotalAmountBsS, format)}",
            565, y + 4, "/F2 9 Tf", "0.15 0.2 0.3 rg", true);

        if (!context.IsSingleCurrency)
        {
            y -= 20;
            WriteRight(sb, $"Diferencial cambiario realizado: {context.SecondaryCurrencySymbol} {MoneyFormat.Number(context.FxDifferenceBsS, format)}", 565, y, "/F1 8.5 Tf", "0.3 0.3 0.3 rg", true);
            y -= 14;
        }
        else
        {
            y -= 14;
        }
        WriteRight(sb, $"Tasa del abono: {MoneyFormat.Number(context.PaymentRate, format, 4)}", 565, y, "/F1 8.5 Tf", "0.3 0.3 0.3 rg", true);

        y -= 22;
        WriteRight(sb, $"Saldo pendiente del cliente: {context.PrimaryCurrencySymbol} {MoneyFormat.Number(context.CurrentDebtUSD, format)}", 565, y, "/F2 9 Tf", "0.1 0.15 0.25 rg", true);
        if (!context.IsSingleCurrency)
        {
            y -= 14;
            WriteRight(sb, $"Equivalente {context.SecondaryCurrencyCode} (tasa del dia {MoneyFormat.Number(context.TodayRate, format, 4)}): {context.SecondaryCurrencySymbol} {MoneyFormat.Number(context.CurrentDebtTodayBsS, format)}", 565, y, "/F1 8.5 Tf", "0.3 0.3 0.3 rg", true);
        }

        WriteLeft(sb, "Sistema de Administracion y Punto de Venta - Comprobante no fiscal", 40, 35, "/F1 8 Tf", "0.5 0.5 0.5 rg");
        return sb.ToString();
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

    private static float Measure(string text, string font, bool bold)
    {
        float size = font switch
        {
            var f when f.Contains("16 Tf") => 16f,
            var f when f.Contains("9.5 Tf") => 9.5f,
            var f when f.Contains("9 Tf") => 9f,
            var f when f.Contains("8.5 Tf") => 8.5f,
            _ => 9f
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
                default:
                    if (c < 128) sb.Append(c);
                    else sb.Append('?');
                    break;
            }
        }
        return sb.ToString();
    }
}
