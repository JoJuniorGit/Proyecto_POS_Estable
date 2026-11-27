using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Core.DTOs;
using Core.Helpers;
using Sales.Module.Receipts;

namespace Sales.Module.Receipts;

/// <summary>
/// PDF nativo (1.4, fuentes Type1) del historial realizado de CxC y de la constancia de
/// cancelacion por obligacion. No recalcula montos: imprime los snapshots historicos ya
/// resueltos por el servicio.
/// </summary>
public static class ReceivablesPdfGenerator
{
    public static byte[] BuildPaidHistory(
        string title,
        IReadOnlyList<string> summaryLines,
        string tableHeader,
        IReadOnlyList<string> rows)
        => BuildPdf(title, summaryLines, tableHeader, rows);

    public static byte[] BuildStatement(ReceivablePaymentTimelineDto timeline)
    {
        ArgumentNullException.ThrowIfNull(timeline);

        string invoice = timeline.InvoiceNumber.HasValue
            ? $"F{timeline.InvoiceNumber.Value:D5}"
            : $"V{timeline.SaleId}";

        var summary = new List<string>
        {
            $"Factura: {invoice}   Cliente: {timeline.CustomerName} ({timeline.CustomerDocument})",
            $"Estado: {timeline.Status}   Emitida: {LocalDate(timeline.IssuedAt)}   Vence: {LocalDate(timeline.DueDate)}",
            $"Original: {Money(timeline.OriginalAmountUsd)} USD   Total abonado: {Money(timeline.TotalPaidUsd)} USD   Saldo final: {Money(timeline.BalanceUsd)} USD"
        };

        var rows = timeline.Payments
            .OrderBy(payment => payment.SequenceNumber)
            .Select(payment =>
            {
                string status = string.Equals(payment.Status, "Anulado", StringComparison.OrdinalIgnoreCase)
                    ? "  [ANULADO]"
                    : string.Empty;
                string rate = payment.ExchangeRateApplied > 0m
                    ? $"  Tasa {Money(payment.ExchangeRateApplied)}"
                    : string.Empty;
                return $"#{payment.SequenceNumber}  {LocalDateTime(payment.PaidAt)}  {payment.PaymentMethodName}  "
                    + $"{payment.PaymentCurrency} {Money(payment.AmountPaidInCurrency)}{rate}  "
                    + $"USD {Money(payment.AmountCreditedUsd)}  Saldo {Money(payment.RunningBalanceAfterUsd)}{status}";
            })
            .ToList();

        return BuildPdf(
            "CONSTANCIA DE CANCELACION / ESTADO DE CUENTA SALDADO",
            summary,
            "ABONO   FECHA/HORA   METODO   MONTO   TASA   ACREDITADO   SALDO",
            rows);
    }

    private static byte[] BuildPdf(
        string title,
        IReadOnlyList<string> summaryLines,
        string tableHeader,
        IReadOnlyList<string> rows)
    {
        const int rowsPerPage = 40;
        int pageCount = Math.Max(1, (int)Math.Ceiling(rows.Count / (double)rowsPerPage));

        var pages = new List<List<string>>();
        for (int page = 0; page < pageCount; page++)
        {
            pages.Add(rows.Skip(page * rowsPerPage).Take(rowsPerPage).ToList());
        }

        var objects = new List<string>
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj",
            "2 0 obj\n<< /Type /Pages /Kids [PAGES] /Count " + pageCount + " >>\nendobj",
            "3 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj",
            "4 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj"
        };

        var kids = new List<string>();
        int nextObject = 5;
        for (int page = 0; page < pageCount; page++)
        {
            int pageObject = nextObject++;
            int contentObject = nextObject++;
            kids.Add($"{pageObject} 0 R");

            string content = BuildPageContent(
                title,
                summaryLines,
                tableHeader,
                pages[page],
                page + 1,
                pageCount);
            byte[] contentBytes = Encoding.ASCII.GetBytes(content);
            objects.Add($"{pageObject} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {contentObject} 0 R >>\nendobj");
            objects.Add($"{contentObject} 0 obj\n<< /Length {contentBytes.Length} >>\nstream\n{content}\nendstream\nendobj");
        }

        objects[1] = "2 0 obj\n<< /Type /Pages /Kids [" + string.Join(" ", kids) + "] /Count " + pageCount + " >>\nendobj";

        using var ms = new MemoryStream();
        using (var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            writer.WriteLine("%PDF-1.4");
            writer.Flush();
        }

        var offsets = new List<long>();
        foreach (string obj in objects)
        {
            offsets.Add(ms.Position);
            byte[] bytes = Encoding.ASCII.GetBytes(obj + "\n");
            ms.Write(bytes, 0, bytes.Length);
        }

        long startXRef = ms.Position;
        using (var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            writer.WriteLine("xref");
            writer.WriteLine($"0 {objects.Count + 1}");
            writer.WriteLine("0000000000 65535 f ");
            foreach (long offset in offsets)
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

        return ms.ToArray();
    }

    private static string BuildPageContent(
        string title,
        IReadOnlyList<string> summaryLines,
        string tableHeader,
        IReadOnlyList<string> rows,
        int page,
        int pageCount)
    {
        var sb = new StringBuilder();
        WriteText(sb, title, 36, 750, "/F2 14 Tf", "0.12 0.11 0.29 rg");

        int y = 728;
        foreach (string line in summaryLines)
        {
            WriteText(sb, line, 36, y, "/F1 8.5 Tf", "0.20 0.25 0.33 rg");
            y -= 13;
        }

        y -= 8;
        sb.AppendLine("0.96 0.97 0.99 rg");
        sb.AppendLine($"36 {y - 4} 540 16 re f");
        WriteText(sb, tableHeader, 40, y, "/F2 8 Tf", "0.20 0.25 0.33 rg");
        y -= 18;

        foreach (string row in rows)
        {
            WriteText(sb, row, 40, y, "/F1 8 Tf", "0.15 0.18 0.24 rg");
            y -= 14;
        }

        WriteText(sb, $"Pagina {page} de {pageCount}", 36, 28, "/F1 8 Tf", "0.45 0.50 0.58 rg");
        return sb.ToString();
    }

    private static void WriteText(StringBuilder sb, string text, int x, int y, string font, string color)
    {
        sb.AppendLine($"{color} BT {font} 1 0 0 1 {x} {y} Tm ({PdfTextEscape.Escape(text)}) Tj ET");
    }

    private static string Money(decimal value) =>
        MoneyFormat.Number(value, MoneyDisplayFormat.Venezuelan, 2);

    private static string LocalDate(DateTime value) =>
        TimeZoneHelper.ToVenezuelaTime(AsUtc(value)).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string LocalDateTime(DateTime value) =>
        TimeZoneHelper.ToVenezuelaTime(AsUtc(value)).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
