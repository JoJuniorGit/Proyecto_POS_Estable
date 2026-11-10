using Sales.Module.DTOs.Profitability;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace Sales.Module.Receipts;

/// <summary>
/// Paso 14 (F3-S5): fila del detalle de Caja Chica por categoría incluida en los reportes
/// descargables. <see cref="AmountLocal"/> conserva el monto en moneda local del gasto y
/// <see cref="ReportAmount"/> es el monto en la moneda de reporte del resumen.
/// </summary>
public sealed record ProfitabilityPdfCategoryRow(
    string CategoryName,
    int Count,
    decimal AmountUsd,
    decimal AmountLocal,
    decimal ReportAmount);

/// <summary>
/// Paso 14 (F3-S3): entrada del generador PDF del Estado de Resultados. La zona horaria se
/// resuelve con el provider del export cuando está disponible; si no, cae a la zona del resumen
/// o a UTC, sin abortar la descarga.
/// </summary>
public sealed record ProfitabilityPdfContext
{
    public ProfitabilitySummaryDto Summary { get; init; } = new();

    public ProfitabilityExportContext Export { get; init; } = new();

    public IReadOnlyList<ProfitabilityPdfCategoryRow> PettyCashCategories { get; init; } = [];

    public TimeZoneInfo? TimeZone { get; init; }
}

/// <summary>
/// Paso 14 (F3-S3): generador PDF 1.4 nativo del Estado de Resultados. Sigue las primitivas
/// vectoriales del proyecto (catalog/pages/Type1 content/xref manual) usadas por el Kardex y los
/// recibos: sin librerías externas, fuentes Helvetica/WinAnsi y paginación con pie
/// "Página X de Y" calculado después de armar todas las páginas.
/// </summary>
public static class ProfitabilityPdfGenerator
{
    private const float PageWidth = 612f;
    private const float PageHeight = 792f;
    private const float Margin = 40f;
    private const float ContentWidth = PageWidth - 2 * Margin;
    private const float BottomLimit = 70f;

    private const string IndigoFill = "0.12 0.11 0.29 rg";
    private const string White = "1 1 1 rg";
    private const string LightIndigo = "0.65 0.71 0.99 rg";
    private const string DarkText = "0.1 0.15 0.25 rg";
    private const string BodyText = "0.2 0.25 0.33 rg";
    private const string MutedText = "0.45 0.5 0.55 rg";
    private const string RowLine = "0.88 0.9 0.92 RG 0.5 w";

    public static byte[] BuildPdf(ProfitabilityPdfContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(context.Summary);
        ArgumentNullException.ThrowIfNull(context.Export);

        var summary = context.Summary;
        var timeZone = ResolveTimeZone(context, summary);
        var builder = new ReportBuilder(context, summary, timeZone);
        builder.WriteHeader();
        builder.WriteKpiCards();
        builder.WriteProfitAndLossTable();
        builder.WritePettyCashSection();
        builder.WriteClosingNote();

        return Assemble(builder.BuildPages());
    }

    private static TimeZoneInfo ResolveTimeZone(ProfitabilityPdfContext context, ProfitabilitySummaryDto summary)
    {
        if (context.TimeZone is not null)
        {
            return context.TimeZone;
        }

        if (!string.IsNullOrWhiteSpace(summary.TimeZoneId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(summary.TimeZoneId);
            }
            catch
            {
                // Same resilient behavior as the CSV/XLSX export: a stale or unknown zone id
                // must not abort the PDF; the timestamps fall back to UTC.
            }
        }

        return TimeZoneInfo.Utc;
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
            return utcFixed;
        }
    }

    private sealed class ReportBuilder
    {
        private readonly List<StringBuilder> _pages = new();
        private readonly ProfitabilityPdfContext _context;
        private readonly ProfitabilitySummaryDto _summary;
        private readonly TimeZoneInfo _timeZone;
        private readonly bool _isUsd;
        private readonly string _moneyFormat;
        private readonly string _currency;
        private StringBuilder _page;
        private float _y;

        public ReportBuilder(
            ProfitabilityPdfContext context,
            ProfitabilitySummaryDto summary,
            TimeZoneInfo timeZone)
        {
            _context = context;
            _summary = summary;
            _timeZone = timeZone;
            _isUsd = string.Equals(summary.ReportCurrencyCode, "USD", StringComparison.OrdinalIgnoreCase);
            _moneyFormat = _isUsd ? "0.0000" : "0.00";
            _currency = string.IsNullOrWhiteSpace(summary.ReportCurrencyCode)
                ? "USD"
                : summary.ReportCurrencyCode;
            _page = new StringBuilder();
            _pages.Add(_page);
        }

        public void WriteHeader()
        {
            FillRect(_page, Margin, 704f, ContentWidth, 52f, IndigoFill);
            WriteLeft(_page, "ESTADO DE RESULTADOS", Margin + 12f, 736f, 16f, true, White);
            var subtitle = $"Estado de Resultados - Reporte de Rentabilidad - {Truncate(_context.Export.CompanyName, 70)}";
            WriteLeft(_page, subtitle, Margin + 12f, 718f, 8.5f, false, LightIndigo);

            FillRect(_page, Margin, 616f, ContentWidth, 76f, "0.97 0.98 0.99 rg");
            StrokeRect(_page, Margin, 616f, ContentWidth, 76f, "0.8 0.84 0.88 RG 1 w");

            var fromLocal = ConvertToLocal(_summary.FromUtc, _timeZone);
            var toLocal = ConvertToLocal(_summary.ToUtc.AddTicks(-1), _timeZone);
            var generatedLocal = ConvertToLocal(_context.Export.GeneratedAtUtc, _timeZone);
            var symbol = string.IsNullOrWhiteSpace(_summary.ReportCurrencySymbol)
                ? _currency
                : _summary.ReportCurrencySymbol;

            WriteLeft(_page, $"Empresa: {Truncate(_context.Export.CompanyName, 70)}", 52f, 678f, 8f, false, BodyText);
            WriteRight(
                _page,
                $"Moneda de reporte: {_currency} ({symbol})",
                PageWidth - 52f,
                678f,
                8f,
                false,
                BodyText);
            WriteLeft(
                _page,
                $"Período local: {fromLocal:dd/MM/yyyy HH:mm} a {toLocal:dd/MM/yyyy HH:mm}",
                52f,
                664f,
                8f,
                false,
                BodyText);
            WriteRight(
                _page,
                $"Generado local: {generatedLocal:dd/MM/yyyy HH:mm:ss}",
                PageWidth - 52f,
                664f,
                8f,
                false,
                BodyText);
            WriteLeft(
                _page,
                $"Generado UTC: {_context.Export.GeneratedAtUtc:dd/MM/yyyy HH:mm:ss}",
                52f,
                650f,
                8f,
                false,
                BodyText);
            WriteRight(
                _page,
                $"Exportado por: {Truncate(_context.Export.ExportedBy, 40)}",
                PageWidth - 52f,
                650f,
                8f,
                false,
                BodyText);
            WriteLeft(_page, $"Zona horaria: {Truncate(_summary.TimeZoneId, 40)}", 52f, 636f, 8f, false, BodyText);
            WriteLeft(
                _page,
                $"Filtros aplicados: {Truncate(_context.Export.FilterSummary, 110)}",
                52f,
                624f,
                7.5f,
                false,
                MutedText);

            _y = 600f;
        }

        public void WriteKpiCards()
        {
            var accrued = _summary.Accrued;
            var realized = _summary.Realized;
            var coverage = _summary.Coverage;
            var cards = new (string Label, string Value, string Detail)[]
            {
                ("UTILIDAD DEVENGADA", Money(accrued.NetProfit), $"Margen neto: {Percent(accrued.NetMarginPercent)}"),
                ("UTILIDAD PERCIBIDA", Money(realized.NetProfit), $"Margen neto: {Percent(realized.NetMarginPercent)}"),
                ("VENTAS NETAS / COBROS", $"{Money(accrued.NetSales)} / {Money(realized.CollectedRevenue)}", "Devengado / Percibido"),
                ("UTILIDAD BRUTA", $"{Money(accrued.GrossProfit)} / {Money(realized.RealizedGrossProfit)}", "Devengado / Percibido"),
                ("UTILIDAD NETA", $"{Money(accrued.NetProfit)} / {Money(realized.NetProfit)}", "Devengado / Percibido"),
                ("COBERTURA COSTOS / FEES", $"{Percent(coverage.CostCoveragePercent)} / {Percent(coverage.FeeCoveragePercent)}", "Cobertura de datos")
            };

            for (int index = 0; index < cards.Length; index++)
            {
                int row = index / 3;
                int column = index % 3;
                float x = Margin + column * 182f;
                float cardY = 538f - row * 64f;
                DrawCard(x, cardY, cards[index]);
            }

            _y = 458f;
        }

        public void WriteProfitAndLossTable()
        {
            WriteSectionTitle("ESTADO DE RESULTADOS COMPARATIVO (DEVENGADO VS. PERCIBIDO)");
            WriteProfitAndLossHeader();

            foreach (var (label, accrued, realized, bold) in BuildProfitAndLossRows())
            {
                EnsureSpace(14f, WriteProfitAndLossHeader);
                DrawProfitAndLossRow(label, accrued, realized, bold);
            }

            _y -= 10f;
        }

        public void WritePettyCashSection()
        {
            EnsureSpace(42f, null);
            WriteSectionTitle("CAJA CHICA POR CATEGORIA (DETALLE)");

            var rows = _context.PettyCashCategories;
            if (rows.Count == 0)
            {
                _y -= 14f;
                WriteLeft(_page, "Sin gastos de Caja Chica en el período.", 48f, _y + 4f, 8f, false, MutedText);
                return;
            }

            WritePettyCashHeader();
            for (int index = 0; index < rows.Count; index++)
            {
                EnsureSpace(14f, WritePettyCashHeader);
                DrawPettyCashRow(rows[index], index % 2 == 1);
            }

            EnsureSpace(18f, WritePettyCashHeader);
            DrawPettyCashTotal(rows);
        }

        public void WriteClosingNote()
        {
            _y -= 8f;
            EnsureSpace(30f, null);
            WriteWrappedParagraph(_summary.GlobalCostsNote, 7f, MutedText);
        }

        public IReadOnlyList<string> BuildPages()
        {
            int total = _pages.Count;
            for (int index = 0; index < _pages.Count; index++)
            {
                AppendFooter(_pages[index], index + 1, total);
            }

            return _pages.Select(page => page.ToString()).ToList();
        }

        private void DrawCard(float x, float y, (string Label, string Value, string Detail) card)
        {
            FillRect(_page, x, y, 168f, 56f, "0.98 0.99 1 rg");
            StrokeRect(_page, x, y, 168f, 56f, "0.82 0.86 0.9 RG 0.8 w");
            WriteLeft(_page, card.Label, x + 8f, y + 43f, 6.8f, true, BodyText);
            WriteLeft(_page, card.Value, x + 8f, y + 23f, 10.5f, true, DarkText);
            WriteLeft(_page, card.Detail, x + 8f, y + 9f, 6.5f, false, MutedText);
        }

        private void WriteSectionTitle(string title)
        {
            _y -= 24f;
            WriteLeft(_page, title, Margin, _y + 8f, 9.5f, true, DarkText);
        }

        private void WriteProfitAndLossHeader()
        {
            _y -= 18f;
            FillRect(_page, Margin, _y, ContentWidth, 18f, "0.2 0.25 0.33 rg");
            WriteLeft(_page, "CONCEPTO", 48f, _y + 5f, 7.5f, true, White);
            WriteRight(_page, $"DEVENGADO ({_currency})", 404f, _y + 5f, 7.5f, true, White);
            WriteRight(_page, $"PERCIBIDO ({_currency})", 564f, _y + 5f, 7.5f, true, White);
        }

        private void DrawProfitAndLossRow(string label, string accrued, string realized, bool bold)
        {
            _y -= 14f;
            WriteLeft(_page, label, 48f, _y + 4f, 8f, bold, DarkText);
            WriteRight(_page, accrued, 404f, _y + 4f, 8f, bold, DarkText);
            WriteRight(_page, realized, 564f, _y + 4f, 8f, bold, DarkText);
            _page.AppendLine($"{RowLine} {Margin} {_y:0.##} {ContentWidth} 0 m 572 {_y:0.##} l S");
        }

        private IEnumerable<(string Label, string Accrued, string Realized, bool Bold)> BuildProfitAndLossRows()
        {
            var accrued = _summary.Accrued;
            var realized = _summary.Realized;

            yield return ("Cobros Totales", "-", Money(realized.CollectedRevenue), false);
            yield return ("Ventas Brutas", Money(accrued.GrossSales), "-", false);
            yield return ("Anulaciones", Money(accrued.Cancellations), "-", false);
            yield return ("Devoluciones", Money(accrued.Returns), "-", false);
            yield return ("Ventas Netas", Money(accrued.NetSales), "-", false);
            yield return (
                "Ingreso con Costo Conocido",
                Money(accrued.KnownCostRevenue),
                Money(realized.KnownCostCollectedRevenue),
                false);
            yield return (
                "Ingreso con Costo Desconocido",
                Money(accrued.UnknownCostRevenue),
                Money(realized.UnknownCostCollectedRevenue),
                false);
            yield return ("CMV", Money(accrued.Cogs), Money(realized.RealizedCogs), false);
            yield return ("Utilidad Bruta", Money(accrued.GrossProfit), Money(realized.RealizedGrossProfit), true);
            yield return (
                "Margen Bruto (%)",
                Percent(accrued.GrossMarginPercent),
                Percent(realized.RealizedGrossMarginPercent),
                false);
            yield return ("Comisiones de Servicio", Money(accrued.ServiceCommissions), Money(realized.ServiceCommissions), false);
            yield return (
                "Diferencia FX (Informativa)",
                Money(accrued.FxDifferenceInformational),
                Money(realized.FxDifference),
                false);
            yield return ("Caja Chica", Money(accrued.PettyCashExpenses), Money(realized.PettyCashExpenses), false);
            yield return (
                "Comisiones de Medios de Pago",
                Money(accrued.PaymentMethodFees),
                Money(realized.PaymentMethodFees),
                false);
            yield return ("Utilidad Neta", Money(accrued.NetProfit), Money(realized.NetProfit), true);
            yield return ("Margen Neto (%)", Percent(accrued.NetMarginPercent), Percent(realized.NetMarginPercent), false);
        }

        private void WritePettyCashHeader()
        {
            _y -= 18f;
            FillRect(_page, Margin, _y, ContentWidth, 18f, "0.2 0.25 0.33 rg");
            WriteLeft(_page, "CATEGORIA", 48f, _y + 5f, 7.5f, true, White);
            WriteRight(_page, "CANT.", 300f, _y + 5f, 7.5f, true, White);
            WriteRight(_page, "Monto USD", 400f, _y + 5f, 7.5f, true, White);
            WriteRight(_page, "Monto local", 480f, _y + 5f, 7.5f, true, White);
            WriteRight(_page, $"Monto {_currency}", 564f, _y + 5f, 7.5f, true, White);
        }

        private void DrawPettyCashRow(ProfitabilityPdfCategoryRow row, bool shaded)
        {
            _y -= 14f;
            if (shaded)
            {
                FillRect(_page, Margin, _y, ContentWidth, 14f, "0.95 0.96 0.97 rg");
            }

            WriteLeft(_page, Truncate(row.CategoryName, 34), 48f, _y + 4f, 8f, false, DarkText);
            WriteRight(_page, row.Count.ToString(CultureInfo.InvariantCulture), 300f, _y + 4f, 8f, false, DarkText);
            WriteRight(_page, row.AmountUsd.ToString("0.0000", CultureInfo.InvariantCulture), 400f, _y + 4f, 8f, false, DarkText);
            WriteRight(_page, row.AmountLocal.ToString("0.00", CultureInfo.InvariantCulture), 480f, _y + 4f, 8f, false, DarkText);
            WriteRight(_page, Money(row.ReportAmount), 564f, _y + 4f, 8f, false, DarkText);
            _page.AppendLine($"{RowLine} {Margin} {_y:0.##} {ContentWidth} 0 m 572 {_y:0.##} l S");
        }

        private void DrawPettyCashTotal(IReadOnlyList<ProfitabilityPdfCategoryRow> rows)
        {
            _y -= 18f;
            FillRect(_page, Margin, _y, ContentWidth, 18f, "0.86 0.9 0.94 rg");
            StrokeRect(_page, Margin, _y, ContentWidth, 18f, "0.7 0.75 0.8 RG 1 w");
            WriteLeft(_page, "TOTAL", 48f, _y + 5f, 8.5f, true, DarkText);
            WriteRight(_page, rows.Sum(row => row.Count).ToString(CultureInfo.InvariantCulture), 300f, _y + 5f, 8.5f, true, DarkText);
            WriteRight(_page, rows.Sum(row => row.AmountUsd).ToString("0.0000", CultureInfo.InvariantCulture), 400f, _y + 5f, 8.5f, true, DarkText);
            WriteRight(_page, rows.Sum(row => row.AmountLocal).ToString("0.00", CultureInfo.InvariantCulture), 480f, _y + 5f, 8.5f, true, DarkText);
            WriteRight(_page, Money(rows.Sum(row => row.ReportAmount)), 564f, _y + 5f, 8.5f, true, DarkText);
        }

        private void EnsureSpace(float height, Action? repeatHeader)
        {
            if (_y - height >= BottomLimit)
            {
                return;
            }

            StartContinuationPage();
            repeatHeader?.Invoke();
        }

        private void StartContinuationPage()
        {
            _page = new StringBuilder();
            _pages.Add(_page);
            FillRect(_page, Margin, 744f, ContentWidth, 30f, IndigoFill);
            WriteLeft(
                _page,
                $"ESTADO DE RESULTADOS - {Truncate(_context.Export.CompanyName, 60)}",
                Margin + 10f,
                756f,
                10f,
                true,
                White);
            WriteLeft(
                _page,
                $"Continuación - Moneda de reporte: {_currency}",
                Margin + 10f,
                746f,
                7.5f,
                false,
                LightIndigo);
            _y = 726f;
        }

        private void WriteWrappedParagraph(string? text, float fontSize, string color)
        {
            const float lineHeight = 10f;
            var words = (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var line = new StringBuilder();

            foreach (var word in words)
            {
                var candidate = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && MeasureTextWidth(candidate, fontSize) > ContentWidth)
                {
                    WriteParagraphLine(line.ToString(), fontSize, color, lineHeight);
                    line.Clear();
                    line.Append(word);
                }
                else
                {
                    line.Clear();
                    line.Append(candidate);
                }
            }

            if (line.Length > 0)
            {
                WriteParagraphLine(line.ToString(), fontSize, color, lineHeight);
            }
        }

        private void WriteParagraphLine(string text, float fontSize, string color, float lineHeight)
        {
            EnsureSpace(lineHeight, null);
            _y -= lineHeight;
            WriteLeft(_page, text, Margin, _y + 2f, fontSize, false, color);
        }

        private string Money(decimal value) => value.ToString(_moneyFormat, CultureInfo.InvariantCulture);

        private static string Percent(decimal value) =>
            value.ToString("0.00", CultureInfo.InvariantCulture) + "%";

        private static void AppendFooter(StringBuilder page, int pageNumber, int pageCount)
        {
            page.AppendLine("0.8 0.84 0.88 RG 0.5 w 40 48 m 572 48 l S");
            WriteLeft(page, "CommandCenter POS - Estado de Resultados", Margin, 30f, 7f, false, "0.5 0.5 0.5 rg");
            WriteRight(page, $"Página {pageNumber} de {pageCount}", PageWidth - Margin, 30f, 7f, false, "0.5 0.5 0.5 rg");
        }
    }

    private static byte[] Assemble(IReadOnlyList<string> pageContents)
    {
        int pageCount = pageContents.Count;
        int firstContentObject = 3 + pageCount;
        int fontRegularObject = 3 + 2 * pageCount;
        int fontBoldObject = fontRegularObject + 1;

        var objects = new List<string>(2 + 2 * pageCount + 2)
        {
            "1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj",
            $"2 0 obj\n<< /Type /Pages /Kids [{string.Join(" ", Enumerable.Range(0, pageCount).Select(i => $"{3 + i} 0 R"))}] /Count {pageCount} >>\nendobj"
        };

        for (int i = 0; i < pageCount; i++)
        {
            objects.Add(
                $"{3 + i} 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {PageWidth:0.##} {PageHeight:0.##}] " +
                $"/Resources << /Font << /F1 {fontRegularObject} 0 R /F2 {fontBoldObject} 0 R >> >> /Contents {firstContentObject + i} 0 R >>\nendobj");
        }

        for (int i = 0; i < pageContents.Count; i++)
        {
            var content = pageContents[i];
            objects.Add(
                $"{firstContentObject + i} 0 obj\n" +
                $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream\nendobj");
        }

        objects.Add($"{fontRegularObject} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>\nendobj");
        objects.Add($"{fontBoldObject} 0 obj\n<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>\nendobj");

        using var ms = new MemoryStream();
        using (var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            writer.WriteLine("%PDF-1.4");
            writer.Flush();
        }

        var offsets = new List<long>(objects.Count);
        foreach (var obj in objects)
        {
            offsets.Add(ms.Position);
            var objBytes = Encoding.ASCII.GetBytes(obj + "\n");
            ms.Write(objBytes, 0, objBytes.Length);
        }

        long startXRef = ms.Position;
        using (var writer = new StreamWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            writer.WriteLine("xref");
            writer.WriteLine($"0 {objects.Count + 1}");
            writer.WriteLine("0000000000 65535 f ");
            foreach (var objectOffset in offsets)
            {
                writer.WriteLine($"{objectOffset:D10} 00000 n ");
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

    private static void FillRect(StringBuilder sb, float x, float y, float width, float height, string color)
    {
        sb.AppendLine(color);
        sb.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0:0.##} {1:0.##} {2:0.##} {3:0.##} re f",
            x,
            y,
            width,
            height));
    }

    private static void StrokeRect(StringBuilder sb, float x, float y, float width, float height, string color)
    {
        sb.AppendLine(color);
        sb.AppendLine(string.Format(
            CultureInfo.InvariantCulture,
            "{0:0.##} {1:0.##} {2:0.##} {3:0.##} re S",
            x,
            y,
            width,
            height));
    }

    private static void WriteLeft(
        StringBuilder sb,
        string text,
        float x,
        float y,
        float fontSize,
        bool bold,
        string color)
    {
        sb.AppendLine("BT");
        sb.AppendLine(bold ? $"/F2 {fontSize:0.##} Tf" : $"/F1 {fontSize:0.##} Tf");
        sb.AppendLine(color);
        sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "{0:0.##} {1:0.##} Td", x, y));
        sb.AppendLine($"({PdfTextEscape.Escape(text)}) Tj");
        sb.AppendLine("ET");
    }

    private static void WriteRight(
        StringBuilder sb,
        string text,
        float rightX,
        float y,
        float fontSize,
        bool bold,
        string color)
    {
        float width = MeasureTextWidth(text, fontSize);
        WriteLeft(sb, text, rightX - width, y, fontSize, bold, color);
    }

    private static float MeasureTextWidth(string? text, float fontSize)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0f;
        }

        float total = 0f;
        float scale = fontSize / 1000f;
        foreach (char c in text)
        {
            float width;
            if (c >= '0' && c <= '9') width = 556f;
            else switch (c)
            {
                case ' ': width = 278f; break;
                case '.': case ',': case ':': case ';': width = 278f; break;
                case '-': case '+': width = 556f; break;
                case '(': case ')': width = 333f; break;
                case 'M': case 'W': width = 833f; break;
                case 'I': case 'i': case 'l': case 't': case '1': width = 278f; break;
                default:
                    width = char.IsUpper(c) ? 667f : 500f;
                    break;
            }

            total += width * scale;
        }

        return total;
    }

    private static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        return text.Length <= maxLength ? text : text[..(maxLength - 3)] + "...";
    }
}
