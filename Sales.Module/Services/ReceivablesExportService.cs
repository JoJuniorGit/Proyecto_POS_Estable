using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Core.DTOs;
using Core.Helpers;
using Sales.Module.Interfaces;
using Sales.Module.Receipts;

namespace Sales.Module.Services;

/// <summary>
/// Exportacion server-side del historial de Deudas Pagadas (CSV, XLSX, PDF) y de la constancia
/// por obligacion. Todos los montos salen de los snapshots historicos; no se reproyecta la tasa.
/// </summary>
public sealed class ReceivablesExportService : IReceivablesExportService
{
    private const string CsvContentType = "text/csv; charset=utf-8";
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string PdfContentType = "application/pdf";

    private readonly ICreditService _creditService;

    public ReceivablesExportService(ICreditService creditService)
    {
        _creditService = creditService ?? throw new ArgumentNullException(nameof(creditService));
    }

    public async Task<ReceivablesExportFile> ExportPaidAsync(
        PaidReceivablesFilterDto filter,
        string mode,
        string format,
        CancellationToken cancellationToken = default)
    {
        filter ??= new PaidReceivablesFilterDto();
        string normalizedMode = (mode ?? "invoice").Trim().ToLowerInvariant();
        string normalizedFormat = (format ?? "csv").Trim().ToLowerInvariant();
        if (normalizedFormat is not ("csv" or "xlsx" or "pdf"))
        {
            throw new ArgumentException(
                $"Formato de exportacion no soportado: '{format}'. Formatos validos: csv, xlsx, pdf.",
                nameof(format));
        }

        bool paymentsMode = normalizedMode == "payment";
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmm", CultureInfo.InvariantCulture);

        if (paymentsMode)
        {
            var (payments, summary) = await LoadAllPaymentsAsync(filter, cancellationToken);
            var rows = BuildPaymentRows(payments);
            byte[] content = normalizedFormat switch
            {
                "xlsx" => BuildPaymentXlsx(rows),
                "pdf" => BuildPaymentPdf(rows, summary),
                _ => BuildCsv(rows)
            };
            string fileName = $"Abonos_DeudasPagadas_{stamp}.{normalizedFormat}";
            return new ReceivablesExportFile(content, ContentTypeFor(normalizedFormat), fileName);
        }
        else
        {
            var (debts, summary) = await LoadAllDebtsAsync(filter, cancellationToken);
            var rows = BuildDebtRows(debts);
            byte[] content = normalizedFormat switch
            {
                "xlsx" => BuildXlsx(rows, "Deudas Pagadas"),
                "pdf" => BuildDebtPdf(rows, summary),
                _ => BuildCsv(rows)
            };
            string fileName = $"DeudasPagadas_{stamp}.{normalizedFormat}";
            return new ReceivablesExportFile(content, ContentTypeFor(normalizedFormat), fileName);
        }
    }

    public async Task<ReceivablesExportFile> ExportStatementAsync(
        int obligationId,
        CancellationToken cancellationToken = default)
    {
        var timeline = await _creditService.GetReceivablePaymentTimelineAsync(obligationId, cancellationToken);
        string invoice = timeline.InvoiceNumber.HasValue
            ? $"F{timeline.InvoiceNumber.Value:D5}"
            : $"V{timeline.SaleId}";
        byte[] content = ReceivablesPdfGenerator.BuildStatement(timeline);
        return new ReceivablesExportFile(content, PdfContentType, $"Constancia_Cancelacion_{invoice}.pdf");
    }

    private async Task<(List<PaidReceivableItemDto> Items, PaidReceivablesSummaryDto Summary)> LoadAllDebtsAsync(
        PaidReceivablesFilterDto filter,
        CancellationToken cancellationToken)
    {
        var items = new List<PaidReceivableItemDto>();
        PaidReceivablesSummaryDto summary = new();
        int page = 1;

        while (true)
        {
            var result = await _creditService.GetPaidReceivablesAsync(new PaidReceivablesFilterDto
            {
                Search = filter.Search,
                PaidFrom = filter.PaidFrom,
                PaidTo = filter.PaidTo,
                PaymentMethodId = filter.PaymentMethodId,
                Page = page,
                PageSize = 200
            }, cancellationToken);

            if (page == 1)
            {
                summary = result.Summary;
            }

            items.AddRange(result.Items);
            if (!result.HasMore)
            {
                break;
            }

            page++;
        }

        return (items, summary);
    }

    private async Task<(List<PaidReceivablePaymentItemDto> Items, PaidReceivablesSummaryDto Summary)> LoadAllPaymentsAsync(
        PaidReceivablesFilterDto filter,
        CancellationToken cancellationToken)
    {
        var items = new List<PaidReceivablePaymentItemDto>();
        PaidReceivablesSummaryDto summary = new();
        int page = 1;

        while (true)
        {
            var result = await _creditService.GetPaidReceivablePaymentsAsync(new PaidReceivablesFilterDto
            {
                Search = filter.Search,
                PaidFrom = filter.PaidFrom,
                PaidTo = filter.PaidTo,
                PaymentMethodId = filter.PaymentMethodId,
                Page = page,
                PageSize = 200
            }, cancellationToken);

            if (page == 1)
            {
                summary = result.Summary;
            }

            items.AddRange(result.Items);
            if (!result.HasMore)
            {
                break;
            }

            page++;
        }

        return (items, summary);
    }

    private static List<string[]> BuildDebtRows(IReadOnlyList<PaidReceivableItemDto> debts)
    {
        var rows = new List<string[]>
        {
            new[] { "Documento", "Cliente", "Documento cliente", "Emision", "Pagada", "Dias", "A tiempo", "Total pagado USD", "Pagado Bs.S historico", "Abonos", "Metodos" }
        };

        rows.AddRange(debts.Select(item => new[]
        {
            item.InvoiceNumber.HasValue ? $"F{item.InvoiceNumber.Value:D5}" : $"V{item.SaleId}",
            item.CustomerName,
            item.CustomerDocument,
            LocalDate(item.IssuedAt),
            LocalDate(item.PaidAt),
            item.DaysToSettle.ToString(CultureInfo.InvariantCulture),
            item.SettledOnTime ? "A tiempo" : "Tras mora",
            Number(item.TotalPaidUsd),
            Number(item.TotalPaidLocalHistorical),
            item.PaymentsCount.ToString(CultureInfo.InvariantCulture),
            item.PaymentMethodsSummary
        }));

        return rows;
    }

    private static List<string[]> BuildPaymentRows(IReadOnlyList<PaidReceivablePaymentItemDto> payments)
    {
        var rows = new List<string[]>
        {
            new[] { "Fecha", "Cajero", "Cliente", "Documento cliente", "Facturas", "Metodo", "Moneda", "Monto cobrado", "Tasa", "Acreditado USD", "Referencia", "Imputacion", "Estado" }
        };

        rows.AddRange(payments.Select(item => new[]
        {
            LocalDateTime(item.PaidAt),
            item.CashierName,
            item.CustomerName,
            item.CustomerDocument,
            item.InvoiceSummary,
            item.PaymentMethodName,
            item.PaymentCurrency,
            Number(item.AmountPaidInCurrency),
            Number(item.ExchangeRateApplied),
            Number(item.AmountCreditedUsd),
            item.Reference ?? string.Empty,
            item.ImputationMode,
            item.Status
        }));

        return rows;
    }

    private static byte[] BuildCsv(IReadOnlyList<string[]> rows)
    {
        var sb = new StringBuilder();
        foreach (string[] row in rows)
        {
            sb.AppendLine(string.Join(';', row.Select(EscapeCsv)));
        }

        byte[] payload = Encoding.UTF8.GetBytes(sb.ToString());
        byte[] bom = Encoding.UTF8.GetPreamble();
        byte[] file = new byte[bom.Length + payload.Length];
        Buffer.BlockCopy(bom, 0, file, 0, bom.Length);
        Buffer.BlockCopy(payload, 0, file, bom.Length, payload.Length);
        return file;
    }

    private static byte[] BuildXlsx(IReadOnlyList<string[]> rows, string sheetName)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add(sheetName);
        for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
        {
            string[] row = rows[rowIndex];
            for (int columnIndex = 0; columnIndex < row.Length; columnIndex++)
            {
                sheet.Cell(rowIndex + 1, columnIndex + 1).Value = row[columnIndex];
            }
        }

        if (rows.Count > 0)
        {
            sheet.Row(1).Style.Font.Bold = true;
            sheet.Row(1).Style.Fill.BackgroundColor = XLColor.FromHtml("#F8FAFC");
        }

        sheet.Columns().AdjustToContents();
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static byte[] BuildPaymentXlsx(IReadOnlyList<string[]> rows) => BuildXlsx(rows, "Abonos");

    private static byte[] BuildDebtPdf(IReadOnlyList<string[]> rows, PaidReceivablesSummaryDto summary)
    {
        var summaryLines = new List<string>
        {
            $"Deudas pagadas: {summary.PaidDebtsCount}   Cobrado USD: {Number(summary.TotalCollectedUsd)}   "
                + $"Cobrado Bs.S historico: {Number(summary.TotalCollectedLocalHistorical)}",
            $"Abonos registrados: {summary.TotalPaymentsCount}   Tiempo promedio: {Number(summary.AverageDaysToSettle, 1)} dias"
        };
        var body = rows.Skip(1)
            .Select(row => string.Join("  |  ", row))
            .ToList();
        return ReceivablesPdfGenerator.BuildPaidHistory(
            "HISTORIAL DE DEUDAS PAGADAS (RECAUDACION REALIZADA)",
            summaryLines,
            rows.Count > 0 ? string.Join("  |  ", rows[0]) : string.Empty,
            body);
    }

    private static byte[] BuildPaymentPdf(IReadOnlyList<string[]> rows, PaidReceivablesSummaryDto summary)
    {
        var summaryLines = new List<string>
        {
            $"Deudas pagadas: {summary.PaidDebtsCount}   Cobrado USD: {Number(summary.TotalCollectedUsd)}   "
                + $"Abonos registrados: {summary.TotalPaymentsCount}",
            "Bitacora plana: cada abono FIFO cuenta una sola vez; las facturas imputadas se listan en la columna Facturas."
        };
        var body = rows.Skip(1)
            .Select(row => string.Join("  |  ", row))
            .ToList();
        return ReceivablesPdfGenerator.BuildPaidHistory(
            "HISTORIAL DE ABONOS DE DEUDAS PAGADAS",
            summaryLines,
            rows.Count > 0 ? string.Join("  |  ", rows[0]) : string.Empty,
            body);
    }

    private static string EscapeCsv(string value)
    {
        string text = value ?? string.Empty;
        return text.Contains(';') || text.Contains('"') || text.Contains('\n')
            ? $"\"{text.Replace("\"", "\"\"")}\""
            : text;
    }

    private static string ContentTypeFor(string format) => format switch
    {
        "xlsx" => XlsxContentType,
        "pdf" => PdfContentType,
        _ => CsvContentType
    };

    private static string Number(decimal value, int decimals = 2) =>
        MoneyFormat.Number(value, MoneyDisplayFormat.Venezuelan, decimals);

    private static string LocalDate(DateTime value) =>
        TimeZoneHelper.ToVenezuelaTime(AsUtc(value)).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string LocalDateTime(DateTime value) =>
        TimeZoneHelper.ToVenezuelaTime(AsUtc(value)).ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);

    private static DateTime AsUtc(DateTime value) =>
        value.Kind == DateTimeKind.Utc ? value : DateTime.SpecifyKind(value, DateTimeKind.Utc);
}
