using Core.DTOs;
using Sales.Module.DTOs.Profitability;
using Sales.Module.Receipts;
using System;
using System.Collections.Generic;
using System.Text;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Paso 14 (F3-WU3, F3-S3/F3-S5): generador PDF 1.4 nativo del Estado de Resultados. Verifica el
/// contrato mínimo (magic, EOF, membrete, KPIs, tabla comparativa y Caja Chica por categoría) y
/// que el pie "Página X de Y" se numera después de conocer la cantidad real de páginas.
/// </summary>
public class ProfitabilityPdfGeneratorTests
{
    private static readonly TimeZoneInfo FixedTimeZone = TimeZoneInfo.CreateCustomTimeZone(
        "ProfitabilityPdf/UTC-4",
        TimeSpan.FromHours(-4),
        "Profitability PDF UTC-4",
        "Profitability PDF UTC-4");

    private static readonly DateTime FromUtc = new(2026, 11, 2, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ToUtc = new(2026, 11, 3, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime GeneratedAtUtc = new(2026, 11, 5, 15, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void BuildPdf_GeneratesHeaderKpisPnlPettyCashAndNumberedFooters()
    {
        var context = CreateContext(categories: BuildCategories(10));

        var pdf = ProfitabilityPdfGenerator.BuildPdf(context);
        var content = Encoding.ASCII.GetString(pdf);
        var readable = Readable(content);

        Assert.StartsWith("%PDF-1.4", content);
        Assert.Contains("%%EOF", content);
        Assert.Contains("Estado de Resultados", content);
        Assert.Contains("Comercializadora Demo", content);
        Assert.Contains("Período local: 02/11/2026 00:00 a 02/11/2026 23:59", readable);
        Assert.Contains("Generado local: 05/11/2026 11:30:00", content);
        Assert.Contains("Generado UTC: 05/11/2026 15:30:00", content);
        Assert.Contains("Exportado por: Ana Gerente", content);
        Assert.Contains("Filtros aplicados", readable);
        Assert.Contains("Custom (2026-11-02 - 2026-11-02)", readable);
        Assert.Contains("Moneda de reporte: USD ($)", readable);

        Assert.Contains("UTILIDAD DEVENGADA", content);
        Assert.Contains("UTILIDAD PERCIBIDA", content);
        Assert.Contains("VENTAS NETAS / COBROS", content);
        Assert.Contains("UTILIDAD BRUTA", content);
        Assert.Contains("UTILIDAD NETA", content);
        Assert.Contains("COBERTURA COSTOS / FEES", content);

        Assert.Contains("ESTADO DE RESULTADOS COMPARATIVO", content);
        Assert.Contains("Ingreso con Costo Conocido", content);
        Assert.Contains("Comisiones de Servicio", content);
        Assert.Contains("Comisiones de Medios de Pago", content);
        Assert.Contains("-11.6500", content);

        Assert.Contains("CAJA CHICA POR CATEGORIA", content);
        Assert.Contains("Varios", content);
        Assert.Contains("InsumosLimpieza", content);
        Assert.Contains("Monto local", content);
        Assert.Contains("Monto USD", content);

        // The 10 petty-cash categories overflow page 1: the footer must know the total up front.
        Assert.Contains("Página 1 de 2", readable);
        Assert.Contains("Página 2 de 2", readable);
        Assert.Equal(2, CountPageObjects(content));
    }

    [Fact]
    public void BuildPdf_EscapesParenthesesAndBackslashesInText()
    {
        var context = CreateContext(
            companyName: @"Demo (Sucursal) \ Central",
            categories: new[] { new ProfitabilityPdfCategoryRow("Varios", 1, 10m, 500m, 10m) });

        var pdf = ProfitabilityPdfGenerator.BuildPdf(context);
        var content = Encoding.ASCII.GetString(pdf);

        Assert.Contains(@"Demo \(Sucursal\) \\ Central", content);
    }

    [Fact]
    public void BuildPdf_SingleCurrency_UsesReportCurrencyAndTwoDecimalFormat()
    {
        var context = CreateContext(
            singleCurrency: true,
            categories: new[] { new ProfitabilityPdfCategoryRow("Varios", 1, 10m, 500m, 500m) });

        var pdf = ProfitabilityPdfGenerator.BuildPdf(context);
        var content = Encoding.ASCII.GetString(pdf);

        Assert.Contains("COP", content);
        Assert.Contains("80.00", content);
        Assert.Contains("500.00", content);
        Assert.DoesNotContain("80.0000", content);
        Assert.Contains("Página 1 de 1", Readable(content));
    }

    private static ProfitabilityPdfContext CreateContext(
        string companyName = "Comercializadora Demo",
        IReadOnlyList<ProfitabilityPdfCategoryRow>? categories = null,
        bool singleCurrency = false) => new()
    {
        Summary = CreateSummary(singleCurrency),
        Export = new ProfitabilityExportContext
        {
            CompanyName = companyName,
            ExportedBy = "Ana Gerente",
            FilterSummary = "Período: Custom (2026-11-02 - 2026-11-02)",
            GeneratedAtUtc = GeneratedAtUtc
        },
        TimeZone = FixedTimeZone,
        PettyCashCategories = categories ?? []
    };

    private static ProfitabilitySummaryDto CreateSummary(bool singleCurrency = false) => new()
    {
        FromUtc = FromUtc,
        ToUtc = ToUtc,
        TimeZoneId = FixedTimeZone.Id,
        MonetaryMode = singleCurrency ? MonetaryMode.Single : MonetaryMode.Dual,
        ReportCurrencyCode = singleCurrency ? "COP" : "USD",
        ReportCurrencySymbol = singleCurrency ? "COL$" : "$",
        IsSingleCurrency = singleCurrency,
        GeneratedAtUtc = GeneratedAtUtc,
        Accrued = new ProfitabilityAccruedDto
        {
            GrossSales = 160m,
            Cancellations = 30m,
            Returns = 50m,
            NetSales = 80m,
            KnownCostRevenue = 20m,
            UnknownCostRevenue = 60m,
            Cogs = 15m,
            GrossProfit = 5m,
            GrossMarginPercent = 25m,
            ServiceCommissions = 1.4m,
            FxDifferenceInformational = 2m,
            PettyCashExpenses = 15.25m,
            PaymentMethodFees = 1.4m,
            NetProfit = -11.65m,
            NetMarginPercent = -14.5625m
        },
        Realized = new ProfitabilityRealizedDto
        {
            CollectedRevenue = 180m,
            KnownCostCollectedRevenue = 130m,
            UnknownCostCollectedRevenue = 50m,
            RealizedCogs = 84m,
            RealizedGrossProfit = 46m,
            RealizedGrossMarginPercent = 35.3846m,
            ServiceCommissions = 1.4m,
            FxDifference = 8m,
            PettyCashExpenses = 15.25m,
            PaymentMethodFees = 4.5m,
            NetProfit = 35.65m,
            NetMarginPercent = 27.4231m
        },
        Coverage = new ProfitabilityCoverageDto
        {
            CostCoveragePercent = 73.6842m,
            KnownCostRevenue = 20m,
            KnownCogs = 15m,
            UnknownCostRevenue = 60m,
            UnknownCostLinesCount = 1,
            FeeCoveragePercent = 100m,
            KnownFeePaymentAmount = 650m,
            UnknownFeePaymentAmount = 0m,
            UnknownFeePaymentsCount = 0
        }
    };

    private static IReadOnlyList<ProfitabilityPdfCategoryRow> BuildCategories(int count)
    {
        var rows = new List<ProfitabilityPdfCategoryRow>(count);
        for (int index = 0; index < count; index++)
        {
            var name = index switch
            {
                0 => "Varios",
                1 => "InsumosLimpieza",
                _ => $"Categoria{index:00}"
            };
            rows.Add(new ProfitabilityPdfCategoryRow(name, index + 1, 10m + index, 500m + index, 10m + index));
        }

        return rows;
    }

    private static string Readable(string content) => content
        .Replace(@"\(", "(")
        .Replace(@"\)", ")")
        .Replace(@"\341", "á")
        .Replace(@"\351", "é")
        .Replace(@"\355", "í")
        .Replace(@"\363", "ó")
        .Replace(@"\372", "ú")
        .Replace(@"\361", "ñ");

    private static int CountPageObjects(string content)
    {
        int count = 0;
        int index = 0;
        while ((index = content.IndexOf("/Type /Page ", index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += "/Type /Page ".Length;
        }

        return count;
    }
}
