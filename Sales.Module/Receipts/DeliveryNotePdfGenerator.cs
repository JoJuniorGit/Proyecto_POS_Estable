using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Core.Helpers;
using Sales.Module.Entities;

namespace Sales.Module.Receipts;

public sealed record DeliveryNotePendingItem(string ProductName, decimal Quantity);

public static class DeliveryNotePdfGenerator
{
    private const int MinimumRowY = 80;

    public static byte[] BuildPdf(
        SaleDelivery delivery,
        Sale sale,
        IReadOnlyList<DeliveryNotePendingItem> pendingItems)
    {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(sale);
        ArgumentNullException.ThrowIfNull(pendingItems);

        var pages = BuildContent(delivery, sale, pendingItems);
        var pageObjectNumbers = Enumerable.Range(0, pages.Count)
            .Select(index => 5 + (index * 2))
            .ToList();
        var pageReferences = string.Join(" ", pageObjectNumbers.Select(number => $"{number} 0 R"));
        var objects = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj",
            $"2 0 obj\n<< /Type /Pages /Kids [{pageReferences}] /Count {pages.Count} >>\nendobj",
            "3 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj",
            "4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj"
        };

        for (int index = 0; index < pages.Count; index++)
        {
            int pageObjectNumber = pageObjectNumbers[index];
            int contentObjectNumber = pageObjectNumber + 1;
            string content = pages[index];
            byte[] contentBytes = Encoding.ASCII.GetBytes(content);
            objects.Add(
                $"{pageObjectNumber} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentObjectNumber} 0 R >>\nendobj");
            objects.Add(
                $"{contentObjectNumber} 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n{content}\nendstream\nendobj");
        }

        using var stream = new MemoryStream();
        using (var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.WriteLine("%PDF-1.4");
            writer.Flush();
        }

        var offsets = new List<long>();
        foreach (var pdfObject in objects)
        {
            offsets.Add(stream.Position);
            byte[] objectBytes = Encoding.ASCII.GetBytes(pdfObject + "\n");
            stream.Write(objectBytes, 0, objectBytes.Length);
        }

        long startXRef = stream.Position;
        using (var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.WriteLine("xref");
            writer.WriteLine($"0 {objects.Count + 1}");
            writer.WriteLine("0000000000 65535 f ");
            foreach (var offset in offsets)
            {
                writer.WriteLine($"{offset:D10} 00000 n ");
            }
            writer.WriteLine("trailer");
            writer.WriteLine($"<< /Size {objects.Count + 1} /Root 1 0 R >>");
            writer.WriteLine("startxref");
            writer.WriteLine(startXRef);
            writer.WriteLine("%%EOF");
            writer.Flush();
        }

        return stream.ToArray();
    }

    private static IReadOnlyList<string> BuildContent(
        SaleDelivery delivery,
        Sale sale,
        IReadOnlyList<DeliveryNotePendingItem> pendingItems)
    {
        var pages = new List<string>();
        var localDeliveredAt = TimeZoneHelper.ToVenezuelaTime(delivery.DeliveredAt);
        int pageNumber = 1;
        var page = CreatePage(delivery, sale, localDeliveredAt, pageNumber, out int y);

        void AddContinuationPage(string sectionTitle)
        {
            AddFooter(page, pageNumber);
            pages.Add(page.ToString());
            pageNumber++;
            page = CreatePage(delivery, sale, localDeliveredAt, pageNumber, out y);
            WriteSection(page, ref y, sectionTitle);
        }

        WriteSection(page, ref y, "ITEMS ENTREGADOS");
        if (delivery.Items.Count == 0)
        {
            WriteRow(page, ref y, "Sin items registrados", 0m);
        }
        else
        {
            foreach (var item in delivery.Items)
            {
                if (y < MinimumRowY)
                {
                    AddContinuationPage("ITEMS ENTREGADOS (CONT.)");
                }

                WriteRow(page, ref y, item.ProductName, item.QuantityDelivered);
            }
        }

        var remainingItems = pendingItems.Where(item => item.Quantity > 0m).ToList();
        if (y < MinimumRowY + 28)
        {
            AddContinuationPage("PENDIENTES RESTANTES");
        }
        else
        {
            WriteSection(page, ref y, "PENDIENTES RESTANTES");
        }

        if (remainingItems.Count == 0)
        {
            WriteRow(page, ref y, "Sin mercancia pendiente", 0m);
        }
        else
        {
            foreach (var item in remainingItems)
            {
                if (y < MinimumRowY)
                {
                    AddContinuationPage("PENDIENTES RESTANTES (CONT.)");
                }

                WriteRow(page, ref y, item.ProductName, item.Quantity);
            }
        }

        if (y < MinimumRowY)
        {
            AddContinuationPage("PENDIENTES RESTANTES (CONT.)");
        }
        WriteRow(page, ref y, "TOTAL PENDIENTE", remainingItems.Sum(item => item.Quantity));

        AddFooter(page, pageNumber);
        pages.Add(page.ToString());
        return pages;
    }

    private static StringBuilder CreatePage(
        SaleDelivery delivery,
        Sale sale,
        DateTime localDeliveredAt,
        int pageNumber,
        out int y)
    {
        var sb = new StringBuilder();
        sb.AppendLine("0.12 0.11 0.29 rg");
        sb.AppendLine("40 700 532 55 re f");
        WriteLeft(sb, pageNumber == 1 ? "NOTA DE DESPACHO" : "NOTA DE DESPACHO (CONTINUACION)", 55, 732, "/F2 16 Tf", "1 1 1 rg");
        WriteLeft(sb, "Comprobante de entrega de mercancía en custodia - NO ES UNA FACTURA FISCAL", 55, 712, "/F1 8 Tf", "0.65 0.71 0.99 rg");

        if (pageNumber == 1)
        {
            sb.AppendLine("0.97 0.98 0.99 rg");
            sb.AppendLine("40 625 532 60 re f");
            sb.AppendLine("0.8 0.84 0.88 RG 1 w");
            sb.AppendLine("40 625 532 60 re S");
            WriteLeft(sb, $"Fecha de retiro: {localDeliveredAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)}", 52, 667, "/F1 9 Tf", "0.2 0.25 0.33 rg");
            WriteLeft(sb, $"Cajero: {delivery.DeliveredByName}", 320, 667, "/F1 9 Tf", "0.2 0.25 0.33 rg");
            WriteLeft(sb, $"Venta: {sale.Id}", 52, 653, "/F1 9 Tf", "0.2 0.25 0.33 rg");
            string invoiceNumber = sale.InvoiceNumber.HasValue
                ? $"#{sale.InvoiceNumber.Value.ToString("D5", CultureInfo.InvariantCulture)}"
                : "PENDIENTE";
            WriteLeft(sb, $"Nro. Factura: {invoiceNumber}", 320, 653, "/F1 9 Tf", "0.2 0.25 0.33 rg");
            if (!string.IsNullOrWhiteSpace(sale.CustomerName))
            {
                WriteLeft(sb, $"Cliente: {sale.CustomerName}", 52, 639, "/F1 9 Tf", "0.2 0.25 0.33 rg");
            }
            if (!string.IsNullOrWhiteSpace(sale.CustomerCedula))
            {
                WriteLeft(sb, $"Cedula: {sale.CustomerCedula}", 320, 639, "/F1 9 Tf", "0.2 0.25 0.33 rg");
            }

            y = 607;
        }
        else
        {
            string invoiceNumber = sale.InvoiceNumber.HasValue
                ? $"#{sale.InvoiceNumber.Value.ToString("D5", CultureInfo.InvariantCulture)}"
                : "PENDIENTE";
            WriteLeft(sb, $"Entrega: {delivery.Id} | Venta: {sale.Id} | Nro. Factura: {invoiceNumber}", 52, 681, "/F1 9 Tf", "0.2 0.25 0.33 rg");
            WriteLeft(sb, $"Fecha de retiro: {localDeliveredAt.ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture)} | Cajero: {delivery.DeliveredByName}", 52, 667, "/F1 9 Tf", "0.2 0.25 0.33 rg");
            y = 641;
        }

        return sb;
    }

    private static void WriteSection(StringBuilder sb, ref int y, string title)
    {
        sb.AppendLine("0.2 0.25 0.33 rg");
        sb.AppendLine($"40 {y - 5} 532 20 re f");
        WriteLeft(sb, title, 48, y + 1, "/F2 8.5 Tf", "1 1 1 rg");
        y -= 25;
    }

    private static void WriteRow(StringBuilder sb, ref int y, string productName, decimal quantity)
    {
        sb.AppendLine($"0.88 0.9 0.92 RG 0.5 w 40 {y - 4} 532 0 m 572 {y - 4} l S");
        WriteLeft(sb, productName, 48, y, "/F1 8.5 Tf", "0.1 0.1 0.1 rg");
        WriteRight(sb, quantity.ToString("0.###", CultureInfo.InvariantCulture), 565, y, "/F1 8.5 Tf", "0.1 0.1 0.1 rg");
        y -= 18;
    }

    private static void AddFooter(StringBuilder sb, int pageNumber)
    {
        WriteLeft(sb, $"Comprobante no fiscal - Pagina {pageNumber}", 40, 35, "/F1 8 Tf", "0.5 0.5 0.5 rg");
    }

    private static void WriteLeft(StringBuilder sb, string text, float x, float y, string font, string color)
    {
        sb.AppendLine("BT");
        sb.AppendLine(font);
        sb.AppendLine(color);
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1:0.##} Td", x, y));
        sb.AppendLine($"({Escape(text)}) Tj");
        sb.AppendLine("ET");
    }

    private static void WriteRight(StringBuilder sb, string text, float rightX, float y, string font, string color)
    {
        float left = rightX - Measure(text, font);
        sb.AppendLine("BT");
        sb.AppendLine(font);
        sb.AppendLine(color);
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1:0.##} Td", left, y));
        sb.AppendLine($"({Escape(text)}) Tj");
        sb.AppendLine("ET");
    }

    private static float Measure(string text, string font)
    {
        float size = font switch
        {
            var value when value.Contains("16 Tf") => 16f,
            var value when value.Contains("9 Tf") => 9f,
            var value when value.Contains("8.5 Tf") => 8.5f,
            _ => 8f
        };
        float scale = size / 1000f;
        float total = 0f;
        foreach (char character in text)
        {
            if (character >= '0' && character <= '9') total += 556f * scale;
            else if (character == ' ') total += 278f * scale;
            else if (character == '.' || character == ',' || character == ':') total += 278f * scale;
            else if (char.IsUpper(character)) total += 667f * scale;
            else total += 500f * scale;
        }
        return total;
    }

    private static string Escape(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var sb = new StringBuilder();
        foreach (char character in text)
        {
            switch (character)
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
                    if (character < 128) sb.Append(character);
                    else sb.Append('?');
                    break;
            }
        }
        return sb.ToString();
    }
}
