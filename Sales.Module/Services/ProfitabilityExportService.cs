using ClosedXML.Excel;
using Core.Helpers;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.DTOs.Profitability;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

/// <summary>
/// Paso 14 (F3-WU2, F3-S2/F3-S5): exportación CSV/XLSX del Estado de Resultados. Reutiliza el
/// resumen del motor de rentabilidad y agrega el detalle de Caja Chica por categoría dentro de la
/// misma ventana UTC que resolvió el resumen. El CSV usa UTF-8 con BOM, delimitador ';' y escape
/// de fórmulas (CWE-1236); el XLSX usa ClosedXML con las cuatro pestañas aprobadas.
/// </summary>
public sealed class ProfitabilityExportService : IProfitabilityExportService
{
    private const string CsvContentType = "text/csv; charset=utf-8";
    private const string XlsxContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private const string PdfPendingMessage = "El formato PDF se habilita en F3-WU3.";

    private readonly IProfitabilityService _profitabilityService;
    private readonly SalesDbContext _context;
    private readonly ITimeZoneProvider _timeZoneProvider;

    public ProfitabilityExportService(
        IProfitabilityService profitabilityService,
        SalesDbContext context,
        ITimeZoneProvider timeZoneProvider)
    {
        ArgumentNullException.ThrowIfNull(profitabilityService);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(timeZoneProvider);

        _profitabilityService = profitabilityService;
        _context = context;
        _timeZoneProvider = timeZoneProvider;
    }

    public async Task<ProfitabilityExportFile> ExportAsync(
        ProfitabilityFilterDto filter,
        string format,
        ProfitabilityExportContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(filter);
        ArgumentNullException.ThrowIfNull(context);

        var normalizedFormat = (format ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedFormat == "pdf")
        {
            throw new NotSupportedException(PdfPendingMessage);
        }

        if (normalizedFormat is not ("csv" or "xlsx"))
        {
            throw new ArgumentException(
                $"Formato de exportación no soportado: '{format}'. Formatos válidos: csv, xlsx, pdf.",
                nameof(format));
        }

        var summary = await _profitabilityService.GetSummaryAsync(filter, cancellationToken);
        var pettyCashRows = await LoadPettyCashByCategoryAsync(summary, cancellationToken);
        var timeZone = await ResolveTimeZoneAsync(cancellationToken);
        var generatedLocal = ConvertToLocal(context.GeneratedAtUtc, timeZone);
        var fileName = BuildFileName(summary, timeZone, normalizedFormat);

        var content = normalizedFormat == "csv"
            ? BuildCsv(summary, pettyCashRows, context, generatedLocal)
            : BuildXlsx(summary, pettyCashRows, context, generatedLocal);

        return new ProfitabilityExportFile
        {
            Content = content,
            ContentType = normalizedFormat == "csv" ? CsvContentType : XlsxContentType,
            FileName = fileName
        };
    }

    private async Task<IReadOnlyList<PettyCashCategoryRow>> LoadPettyCashByCategoryAsync(
        ProfitabilitySummaryDto summary,
        CancellationToken cancellationToken)
    {
        // F3-S5: the detail is a report section, but the headline PettyCashExpenses stays global
        // and is never prorated per product. The window is the strict [fromUtc, toUtc) already
        // resolved by the summary, so a preset and a custom range behave identically.
        var rows = await _context.PettyCashExpenses
            .AsNoTracking()
            .Where(expense =>
                expense.Status == PettyCashStatus.Active
                && expense.CreatedAtUtc >= summary.FromUtc
                && expense.CreatedAtUtc < summary.ToUtc)
            .GroupBy(expense => expense.Category)
            .Select(group => new
            {
                Category = group.Key,
                Count = group.Count(),
                AmountUsd = group.Sum(expense => expense.AmountUsd),
                AmountBsS = group.Sum(expense => expense.AmountBsS)
            })
            .ToListAsync(cancellationToken);

        return rows
            .OrderBy(row => row.Category.ToString(), StringComparer.Ordinal)
            .Select(row => new PettyCashCategoryRow(
                row.Category.ToString(),
                row.Count,
                row.AmountUsd,
                row.AmountBsS,
                summary.IsSingleCurrency ? row.AmountBsS : row.AmountUsd))
            .ToList();
    }

    private async Task<TimeZoneInfo> ResolveTimeZoneAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _timeZoneProvider.GetTimeZoneInfoAsync(cancellationToken);
        }
        catch
        {
            // Same documented fallback as the Kardex export: a corrupt setting must not abort the
            // download; the local timestamp falls back to the Venezuela zone.
            return TimeZoneHelper.GetVenezuelaTimeZone();
        }
    }

    private static DateTime ConvertToLocal(DateTime utc, TimeZoneInfo timeZone)
    {
        var utcFixed = utc.Kind == DateTimeKind.Utc ? utc : DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        try
        {
            return TimeZoneInfo.ConvertTimeFromUtc(utcFixed, timeZone);
        }
        catch
        {
            return utcFixed.ToLocalTime();
        }
    }

    private static string BuildFileName(
        ProfitabilitySummaryDto summary,
        TimeZoneInfo timeZone,
        string format)
    {
        // The report range is local: the exclusive toUtc is pulled back one tick so a one-day
        // window renders as 20261102_20261102 instead of leaking into the following day.
        var fromLocal = ConvertToLocal(summary.FromUtc, timeZone);
        var toLocal = ConvertToLocal(summary.ToUtc.AddTicks(-1), timeZone);
        return $"Estado_Resultados_{fromLocal:yyyyMMdd}_{toLocal:yyyyMMdd}.{format}";
    }

    private static byte[] BuildCsv(
        ProfitabilitySummaryDto summary,
        IReadOnlyList<PettyCashCategoryRow> pettyCashRows,
        ProfitabilityExportContext context,
        DateTime generatedLocal)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ESTADO DE RESULTADOS");
        sb.AppendLine($"Empresa;{EscapeCsvField(context.CompanyName)}");
        sb.AppendLine($"Generado (UTC);{FormatTimestamp(context.GeneratedAtUtc)}");
        sb.AppendLine($"Generado (hora local);{FormatTimestamp(generatedLocal)}");
        sb.AppendLine($"Exportado por;{EscapeCsvField(context.ExportedBy)}");
        sb.AppendLine($"Zona horaria;{EscapeCsvField(summary.TimeZoneId)}");
        sb.AppendLine($"Moneda de reporte;{EscapeCsvField($"{summary.ReportCurrencyCode} ({summary.ReportCurrencySymbol})")}");
        sb.AppendLine($"Período (UTC);{FormatTimestamp(summary.FromUtc)} a {FormatTimestamp(summary.ToUtc)}");
        sb.AppendLine($"Filtros aplicados;{EscapeCsvField(context.FilterSummary)}");

        sb.AppendLine();
        sb.AppendLine("RESUMEN DEVENGADO");
        sb.AppendLine($"Concepto;Valor ({summary.ReportCurrencyCode})");
        var accrued = summary.Accrued;
        AppendMoneyRow(sb, "Ventas Brutas", accrued.GrossSales, summary);
        AppendMoneyRow(sb, "Anulaciones", accrued.Cancellations, summary);
        AppendMoneyRow(sb, "Devoluciones", accrued.Returns, summary);
        AppendMoneyRow(sb, "Ventas Netas", accrued.NetSales, summary);
        AppendMoneyRow(sb, "Ingreso con Costo Conocido", accrued.KnownCostRevenue, summary);
        AppendMoneyRow(sb, "Ingreso con Costo Desconocido", accrued.UnknownCostRevenue, summary);
        AppendMoneyRow(sb, "CMV", accrued.Cogs, summary);
        AppendMoneyRow(sb, "Utilidad Bruta", accrued.GrossProfit, summary);
        AppendPercentRow(sb, "Margen Bruto (%)", accrued.GrossMarginPercent);
        AppendMoneyRow(sb, "Comisiones de Servicio", accrued.ServiceCommissions, summary);
        AppendMoneyRow(sb, "Diferencia FX (Informativa)", accrued.FxDifferenceInformational, summary);
        AppendMoneyRow(sb, "Caja Chica", accrued.PettyCashExpenses, summary);
        AppendMoneyRow(sb, "Comisiones de Medios de Pago", accrued.PaymentMethodFees, summary);
        AppendMoneyRow(sb, "Utilidad Neta", accrued.NetProfit, summary);
        AppendPercentRow(sb, "Margen Neto (%)", accrued.NetMarginPercent);

        sb.AppendLine();
        sb.AppendLine("RESUMEN PERCIBIDO");
        sb.AppendLine($"Concepto;Valor ({summary.ReportCurrencyCode})");
        var realized = summary.Realized;
        AppendMoneyRow(sb, "Cobros Totales", realized.CollectedRevenue, summary);
        AppendMoneyRow(sb, "Ingreso con Costo Conocido", realized.KnownCostCollectedRevenue, summary);
        AppendMoneyRow(sb, "Ingreso con Costo Desconocido", realized.UnknownCostCollectedRevenue, summary);
        AppendMoneyRow(sb, "CMV Percibido", realized.RealizedCogs, summary);
        AppendMoneyRow(sb, "Utilidad Bruta Percibida", realized.RealizedGrossProfit, summary);
        AppendPercentRow(sb, "Margen Bruto Percibido (%)", realized.RealizedGrossMarginPercent);
        AppendMoneyRow(sb, "Comisiones de Servicio", realized.ServiceCommissions, summary);
        AppendMoneyRow(sb, "Diferencia FX", realized.FxDifference, summary);
        AppendMoneyRow(sb, "Caja Chica", realized.PettyCashExpenses, summary);
        AppendMoneyRow(sb, "Comisiones de Medios de Pago", realized.PaymentMethodFees, summary);
        AppendMoneyRow(sb, "Utilidad Neta", realized.NetProfit, summary);
        AppendPercentRow(sb, "Margen Neto (%)", realized.NetMarginPercent);

        sb.AppendLine();
        sb.AppendLine("COBERTURA");
        sb.AppendLine("Indicador;Valor");
        AppendPercentRow(sb, "Cobertura de Costos (%)", summary.Coverage.CostCoveragePercent);
        AppendMoneyRow(sb, "Ingreso con Costo Conocido", summary.Coverage.KnownCostRevenue, summary);
        AppendMoneyRow(sb, "CMV Conocido", summary.Coverage.KnownCogs, summary);
        AppendMoneyRow(sb, "Ingreso con Costo Desconocido", summary.Coverage.UnknownCostRevenue, summary);
        sb.AppendLine($"Líneas con Costo Desconocido;{summary.Coverage.UnknownCostLinesCount.ToString(CultureInfo.InvariantCulture)}");
        AppendPercentRow(sb, "Cobertura de Comisiones (%)", summary.Coverage.FeeCoveragePercent);
        AppendMoneyRow(sb, "Base de Comisión Conocida", summary.Coverage.KnownFeePaymentAmount, summary);
        AppendMoneyRow(sb, "Base de Comisión Desconocida", summary.Coverage.UnknownFeePaymentAmount, summary);
        sb.AppendLine($"Pagos sin Regla de Comisión;{summary.Coverage.UnknownFeePaymentsCount.ToString(CultureInfo.InvariantCulture)}");

        sb.AppendLine();
        sb.AppendLine("PRODUCTOS Y CATEGORIAS");
        sb.AppendLine("ID Producto;Producto;ID Categoría;Categoría;Cantidad;Ingreso;CMV;Utilidad Bruta;Margen Bruto (%)");
        foreach (var product in summary.Breakdown.Products)
        {
            sb.AppendLine(string.Join(";", new[]
            {
                product.ProductId.ToString(CultureInfo.InvariantCulture),
                EscapeCsvField(product.ProductName),
                product.CategoryId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                EscapeCsvField(product.CategoryName),
                product.QuantitySold.ToString("0.###", CultureInfo.InvariantCulture),
                FormatMoney(product.Revenue, summary),
                FormatMoney(product.Cogs, summary),
                FormatMoney(product.GrossProfit, summary),
                FormatPercent(product.GrossMarginPercent)
            }));
        }

        sb.AppendLine();
        sb.AppendLine("CATEGORIAS");
        sb.AppendLine("ID Categoría;Categoría;Ingreso;CMV;Utilidad Bruta;Margen Bruto (%);Contribución (%)");
        foreach (var category in summary.Breakdown.Categories)
        {
            sb.AppendLine(string.Join(";", new[]
            {
                category.CategoryId.ToString(CultureInfo.InvariantCulture),
                EscapeCsvField(category.CategoryName),
                FormatMoney(category.Revenue, summary),
                FormatMoney(category.Cogs, summary),
                FormatMoney(category.GrossProfit, summary),
                FormatPercent(category.GrossMarginPercent),
                FormatPercent(category.ContributionPercent)
            }));
        }

        sb.AppendLine();
        sb.AppendLine("MEDIOS DE PAGO");
        sb.AppendLine("ID Método;Método;Cobrado;Comisión;Base con Fee Conocida;Base sin Fee Conocida;Cobertura de Fee (%)");
        foreach (var method in summary.Breakdown.PaymentMethods)
        {
            sb.AppendLine(string.Join(";", new[]
            {
                method.PaymentMethodId.ToString(CultureInfo.InvariantCulture),
                EscapeCsvField(method.PaymentMethodName),
                FormatMoney(method.CollectedAmount, summary),
                FormatMoney(method.FeeAmount, summary),
                FormatMoney(method.KnownFeeAmount, summary),
                FormatMoney(method.UnknownFeeAmount, summary),
                FormatPercent(method.FeeCoveragePercent)
            }));
        }

        sb.AppendLine();
        sb.AppendLine("CAJA CHICA POR CATEGORIA");
        sb.AppendLine($"Categoría;Cantidad;Monto USD;Monto Bs.S;Monto {summary.ReportCurrencyCode}");
        foreach (var row in pettyCashRows)
        {
            sb.AppendLine(string.Join(";", new[]
            {
                EscapeCsvField(row.CategoryName),
                row.Count.ToString(CultureInfo.InvariantCulture),
                row.AmountUsd.ToString("0.0000", CultureInfo.InvariantCulture),
                row.AmountBsS.ToString("0.00", CultureInfo.InvariantCulture),
                FormatMoney(row.ReportAmount, summary)
            }));
        }

        sb.AppendLine();
        sb.AppendLine(EscapeCsvField(ProfitabilitySummaryDto.GlobalCostsNoteText));

        // Repo precedent (StockKardexService.Export): ';' delimiter and UTF-8 BOM so Excel opens
        // the es-VE export with the right encoding.
        return Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
    }

    private static byte[] BuildXlsx(
        ProfitabilitySummaryDto summary,
        IReadOnlyList<PettyCashCategoryRow> pettyCashRows,
        ProfitabilityExportContext context,
        DateTime generatedLocal)
    {
        var moneyFormat = IsUsdReport(summary) ? "#,##0.0000" : "#,##0.00";
        using var workbook = new XLWorkbook();

        BuildExecutiveSheet(workbook, summary, context, generatedLocal, moneyFormat);
        BuildProductSheet(workbook, summary, moneyFormat);
        BuildPaymentMethodSheet(workbook, summary, moneyFormat);
        BuildPettyCashSheet(workbook, summary, pettyCashRows, moneyFormat);

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void BuildExecutiveSheet(
        XLWorkbook workbook,
        ProfitabilitySummaryDto summary,
        ProfitabilityExportContext context,
        DateTime generatedLocal,
        string moneyFormat)
    {
        var worksheet = workbook.Worksheets.Add("Resumen Ejecutivo");
        WriteTitle(worksheet, 1, "ESTADO DE RESULTADOS", 3);

        int row = 2;
        row = WriteMetadata(worksheet, row, "Empresa:", context.CompanyName);
        row = WriteMetadata(worksheet, row, "Generado (UTC):", FormatTimestamp(context.GeneratedAtUtc));
        row = WriteMetadata(worksheet, row, "Generado (hora local):", FormatTimestamp(generatedLocal));
        row = WriteMetadata(worksheet, row, "Exportado por:", context.ExportedBy);
        row = WriteMetadata(worksheet, row, "Zona horaria:", summary.TimeZoneId);
        row = WriteMetadata(worksheet, row, "Moneda de reporte:", $"{summary.ReportCurrencyCode} ({summary.ReportCurrencySymbol})");
        row = WriteMetadata(worksheet, row, "Período (UTC):", $"{FormatTimestamp(summary.FromUtc)} a {FormatTimestamp(summary.ToUtc)}");
        row = WriteMetadata(worksheet, row, "Filtros aplicados:", context.FilterSummary);

        row++;
        row = WriteSectionTitle(worksheet, row, "RESUMEN DEVENGADO", 2);
        row = WriteHeaderRow(worksheet, row, "Concepto", $"Valor ({summary.ReportCurrencyCode})");
        var accrued = summary.Accrued;
        row = WriteMoneyRows(worksheet, row, moneyFormat, new[]
        {
            ("Ventas Brutas", accrued.GrossSales),
            ("Anulaciones", accrued.Cancellations),
            ("Devoluciones", accrued.Returns),
            ("Ventas Netas", accrued.NetSales),
            ("Ingreso con Costo Conocido", accrued.KnownCostRevenue),
            ("Ingreso con Costo Desconocido", accrued.UnknownCostRevenue),
            ("CMV", accrued.Cogs),
            ("Utilidad Bruta", accrued.GrossProfit),
            ("Comisiones de Servicio", accrued.ServiceCommissions),
            ("Diferencia FX (Informativa)", accrued.FxDifferenceInformational),
            ("Caja Chica", accrued.PettyCashExpenses),
            ("Comisiones de Medios de Pago", accrued.PaymentMethodFees),
            ("Utilidad Neta", accrued.NetProfit)
        });
        row = WritePercentRow(worksheet, row, "Margen Bruto (%)", accrued.GrossMarginPercent);
        row = WritePercentRow(worksheet, row, "Margen Neto (%)", accrued.NetMarginPercent);

        row++;
        row = WriteSectionTitle(worksheet, row, "RESUMEN PERCIBIDO", 2);
        row = WriteHeaderRow(worksheet, row, "Concepto", $"Valor ({summary.ReportCurrencyCode})");
        var realized = summary.Realized;
        row = WriteMoneyRows(worksheet, row, moneyFormat, new[]
        {
            ("Cobros Totales", realized.CollectedRevenue),
            ("Ingreso con Costo Conocido", realized.KnownCostCollectedRevenue),
            ("Ingreso con Costo Desconocido", realized.UnknownCostCollectedRevenue),
            ("CMV Percibido", realized.RealizedCogs),
            ("Utilidad Bruta Percibida", realized.RealizedGrossProfit),
            ("Comisiones de Servicio", realized.ServiceCommissions),
            ("Diferencia FX", realized.FxDifference),
            ("Caja Chica", realized.PettyCashExpenses),
            ("Comisiones de Medios de Pago", realized.PaymentMethodFees),
            ("Utilidad Neta", realized.NetProfit)
        });
        row = WritePercentRow(worksheet, row, "Margen Bruto Percibido (%)", realized.RealizedGrossMarginPercent);
        row = WritePercentRow(worksheet, row, "Margen Neto (%)", realized.NetMarginPercent);

        row++;
        row = WriteSectionTitle(worksheet, row, "COBERTURA", 2);
        row = WriteHeaderRow(worksheet, row, "Indicador", "Valor");
        row = WriteMoneyRows(worksheet, row, moneyFormat, new[]
        {
            ("Ingreso con Costo Conocido", summary.Coverage.KnownCostRevenue),
            ("CMV Conocido", summary.Coverage.KnownCogs),
            ("Ingreso con Costo Desconocido", summary.Coverage.UnknownCostRevenue),
            ("Base de Comisión Conocida", summary.Coverage.KnownFeePaymentAmount),
            ("Base de Comisión Desconocida", summary.Coverage.UnknownFeePaymentAmount)
        });
        row = WritePercentRow(worksheet, row, "Cobertura de Costos (%)", summary.Coverage.CostCoveragePercent);
        row = WritePercentRow(worksheet, row, "Cobertura de Comisiones (%)", summary.Coverage.FeeCoveragePercent);
        worksheet.Cell(row, 1).Value = "Líneas con Costo Desconocido";
        worksheet.Cell(row, 2).Value = summary.Coverage.UnknownCostLinesCount;
        row++;
        worksheet.Cell(row, 1).Value = "Pagos sin Regla de Comisión";
        worksheet.Cell(row, 2).Value = summary.Coverage.UnknownFeePaymentsCount;

        row += 2;
        worksheet.Cell(row, 1).Value = ProfitabilitySummaryDto.GlobalCostsNoteText;
        worksheet.Range(row, 1, row, 3).Merge();
        worksheet.Cell(row, 1).Style.Font.Italic = true;
        worksheet.Cell(row, 1).Style.Alignment.WrapText = true;

        worksheet.Columns().AdjustToContents();
    }

    private static void BuildProductSheet(
        XLWorkbook workbook,
        ProfitabilitySummaryDto summary,
        string moneyFormat)
    {
        var worksheet = workbook.Worksheets.Add("Por Producto y Categoría");
        WriteTitle(worksheet, 1, "POR PRODUCTO Y CATEGORÍA", 9);

        int row = 3;
        row = WriteHeaderRow(
            worksheet,
            row,
            "ID Producto", "Producto", "ID Categoría", "Categoría", "Cantidad",
            "Ingreso", "CMV", "Utilidad Bruta", "Margen Bruto (%)");
        foreach (var product in summary.Breakdown.Products)
        {
            worksheet.Cell(row, 1).Value = product.ProductId;
            worksheet.Cell(row, 2).Value = product.ProductName;
            if (product.CategoryId.HasValue)
            {
                worksheet.Cell(row, 3).Value = product.CategoryId.Value;
            }

            worksheet.Cell(row, 4).Value = product.CategoryName ?? string.Empty;
            worksheet.Cell(row, 5).Value = product.QuantitySold;
            worksheet.Cell(row, 5).Style.NumberFormat.Format = "0.###";
            WriteMoneyCell(worksheet.Cell(row, 6), product.Revenue, moneyFormat);
            WriteMoneyCell(worksheet.Cell(row, 7), product.Cogs, moneyFormat);
            WriteMoneyCell(worksheet.Cell(row, 8), product.GrossProfit, moneyFormat);
            WritePercentCell(worksheet.Cell(row, 9), product.GrossMarginPercent);
            row++;
        }

        row++;
        row = WriteSectionTitle(worksheet, row, "CATEGORIAS", 7);
        row = WriteHeaderRow(
            worksheet,
            row,
            "ID Categoría", "Categoría", "Ingreso", "CMV", "Utilidad Bruta",
            "Margen Bruto (%)", "Contribución (%)");
        foreach (var category in summary.Breakdown.Categories)
        {
            worksheet.Cell(row, 1).Value = category.CategoryId;
            worksheet.Cell(row, 2).Value = category.CategoryName;
            WriteMoneyCell(worksheet.Cell(row, 3), category.Revenue, moneyFormat);
            WriteMoneyCell(worksheet.Cell(row, 4), category.Cogs, moneyFormat);
            WriteMoneyCell(worksheet.Cell(row, 5), category.GrossProfit, moneyFormat);
            WritePercentCell(worksheet.Cell(row, 6), category.GrossMarginPercent);
            WritePercentCell(worksheet.Cell(row, 7), category.ContributionPercent);
            row++;
        }

        worksheet.Columns().AdjustToContents();
    }

    private static void BuildPaymentMethodSheet(
        XLWorkbook workbook,
        ProfitabilitySummaryDto summary,
        string moneyFormat)
    {
        var worksheet = workbook.Worksheets.Add("Medios de Pago");
        WriteTitle(worksheet, 1, "MEDIOS DE PAGO", 7);

        int row = 3;
        row = WriteHeaderRow(
            worksheet,
            row,
            "ID Método", "Método", "Cobrado", "Comisión", "Base con Fee Conocida",
            "Base sin Fee Conocida", "Cobertura de Fee (%)");
        foreach (var method in summary.Breakdown.PaymentMethods)
        {
            worksheet.Cell(row, 1).Value = method.PaymentMethodId;
            worksheet.Cell(row, 2).Value = method.PaymentMethodName;
            WriteMoneyCell(worksheet.Cell(row, 3), method.CollectedAmount, moneyFormat);
            WriteMoneyCell(worksheet.Cell(row, 4), method.FeeAmount, moneyFormat);
            WriteMoneyCell(worksheet.Cell(row, 5), method.KnownFeeAmount, moneyFormat);
            WriteMoneyCell(worksheet.Cell(row, 6), method.UnknownFeeAmount, moneyFormat);
            WritePercentCell(worksheet.Cell(row, 7), method.FeeCoveragePercent);
            row++;
        }

        worksheet.Columns().AdjustToContents();
    }

    private static void BuildPettyCashSheet(
        XLWorkbook workbook,
        ProfitabilitySummaryDto summary,
        IReadOnlyList<PettyCashCategoryRow> pettyCashRows,
        string moneyFormat)
    {
        var worksheet = workbook.Worksheets.Add("Caja Chica por Categoría");
        WriteTitle(worksheet, 1, "CAJA CHICA POR CATEGORÍA", 5);

        int row = 3;
        row = WriteHeaderRow(
            worksheet,
            row,
            "Categoría", "Cantidad", "Monto USD", "Monto Bs.S", $"Monto {summary.ReportCurrencyCode}");
        foreach (var pettyRow in pettyCashRows)
        {
            worksheet.Cell(row, 1).Value = pettyRow.CategoryName;
            worksheet.Cell(row, 2).Value = pettyRow.Count;
            WriteMoneyCell(worksheet.Cell(row, 3), pettyRow.AmountUsd, "#,##0.0000");
            WriteMoneyCell(worksheet.Cell(row, 4), pettyRow.AmountBsS, "#,##0.00");
            WriteMoneyCell(worksheet.Cell(row, 5), pettyRow.ReportAmount, moneyFormat);
            row++;
        }

        worksheet.Cell(row, 1).Value = "TOTAL";
        worksheet.Cell(row, 1).Style.Font.Bold = true;
        worksheet.Cell(row, 2).Value = pettyCashRows.Sum(pettyRow => pettyRow.Count);
        WriteMoneyCell(worksheet.Cell(row, 3), pettyCashRows.Sum(pettyRow => pettyRow.AmountUsd), "#,##0.0000");
        WriteMoneyCell(worksheet.Cell(row, 4), pettyCashRows.Sum(pettyRow => pettyRow.AmountBsS), "#,##0.00");
        WriteMoneyCell(worksheet.Cell(row, 5), pettyCashRows.Sum(pettyRow => pettyRow.ReportAmount), moneyFormat);

        worksheet.Columns().AdjustToContents();
    }

    private static void WriteTitle(IXLWorksheet worksheet, int row, string title, int lastColumn)
    {
        worksheet.Cell(row, 1).Value = title;
        worksheet.Range(row, 1, row, lastColumn).Merge();
        worksheet.Cell(row, 1).Style.Font.Bold = true;
        worksheet.Cell(row, 1).Style.Font.FontSize = 14;
    }

    private static int WriteMetadata(IXLWorksheet worksheet, int row, string label, string value)
    {
        worksheet.Cell(row, 1).Value = label;
        worksheet.Cell(row, 1).Style.Font.Bold = true;
        worksheet.Cell(row, 2).Value = value;
        return row + 1;
    }

    private static int WriteSectionTitle(IXLWorksheet worksheet, int row, string title, int lastColumn)
    {
        worksheet.Cell(row, 1).Value = title;
        worksheet.Cell(row, 1).Style.Font.Bold = true;
        worksheet.Cell(row, 1).Style.Font.FontSize = 12;
        if (lastColumn > 1)
        {
            worksheet.Range(row, 1, row, lastColumn).Merge();
        }

        return row + 1;
    }

    private static int WriteHeaderRow(IXLWorksheet worksheet, int row, params string[] headers)
    {
        for (int column = 0; column < headers.Length; column++)
        {
            worksheet.Cell(row, column + 1).Value = headers[column];
        }

        var range = worksheet.Range(row, 1, row, headers.Length);
        range.Style.Font.Bold = true;
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#1E88E5");
        return row + 1;
    }

    private static int WriteMoneyRows(
        IXLWorksheet worksheet,
        int row,
        string moneyFormat,
        IEnumerable<(string Label, decimal Value)> rows)
    {
        foreach (var (label, value) in rows)
        {
            worksheet.Cell(row, 1).Value = label;
            WriteMoneyCell(worksheet.Cell(row, 2), value, moneyFormat);
            row++;
        }

        return row;
    }

    private static int WritePercentRow(IXLWorksheet worksheet, int row, string label, decimal value)
    {
        worksheet.Cell(row, 1).Value = label;
        WritePercentCell(worksheet.Cell(row, 2), value);
        return row + 1;
    }

    private static void WriteMoneyCell(IXLCell cell, decimal value, string format)
    {
        cell.Value = value;
        cell.Style.NumberFormat.Format = format;
    }

    private static void WritePercentCell(IXLCell cell, decimal value)
    {
        cell.Value = value;
        // Values arrive as percentage points (25 = 25%); the literal suffix keeps them readable
        // without silently rescaling the report's contract.
        cell.Style.NumberFormat.Format = "0.00\"%\"";
    }

    private static void AppendMoneyRow(
        StringBuilder sb,
        string label,
        decimal value,
        ProfitabilitySummaryDto summary) =>
        sb.AppendLine($"{EscapeCsvField(label)};{FormatMoney(value, summary)}");

    private static void AppendPercentRow(StringBuilder sb, string label, decimal value) =>
        sb.AppendLine($"{EscapeCsvField(label)};{FormatPercent(value)}");

    private static string FormatMoney(decimal value, ProfitabilitySummaryDto summary) =>
        value.ToString(IsUsdReport(summary) ? "0.0000" : "0.00", CultureInfo.InvariantCulture);

    private static string FormatPercent(decimal value) =>
        value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string FormatTimestamp(DateTime value) =>
        value.ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture);

    private static bool IsUsdReport(ProfitabilitySummaryDto summary) =>
        string.Equals(summary.ReportCurrencyCode, "USD", StringComparison.OrdinalIgnoreCase);

    private static string EscapeCsvField(string? field)
    {
        if (string.IsNullOrEmpty(field))
        {
            return string.Empty;
        }

        // CWE-1236 precedent (StockKardexService.Export): neutralize NUL, leading spaces and
        // formula starts that Excel would evaluate as code.
        field = field.Replace("\0", string.Empty);
        var trimmedStart = field.TrimStart();
        bool hadLeadingSpace = trimmedStart.Length != field.Length;

        if (hadLeadingSpace
            || field.StartsWith('=') || field.StartsWith('+')
            || field.StartsWith('-') || field.StartsWith('@')
            || field.StartsWith('\t') || field.StartsWith('\r'))
        {
            field = "'" + trimmedStart;
        }

        if (field.Contains(';') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
        {
            return "\"" + field.Replace("\"", "\"\"") + "\"";
        }

        return field;
    }

    private sealed record PettyCashCategoryRow(
        string CategoryName,
        int Count,
        decimal AmountUsd,
        decimal AmountBsS,
        decimal ReportAmount);
}
