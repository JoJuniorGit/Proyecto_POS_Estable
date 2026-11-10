using ClosedXML.Excel;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Interfaces;
using Moq;
using Sales.Module.Data;
using Sales.Module.DTOs.Profitability;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using Sales.Module.Services;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Paso 14 (F3-WU2, F3-S2/F3-S5): exportación server-side CSV/XLSX del Estado de Resultados con
/// el detalle aprobado de Caja Chica por categoría. La ventana de la consulta de caja chica es la
/// misma [fromUtc, toUtc) que resolvió el resumen y la moneda de reporte sigue el régimen.
/// </summary>
public class ProfitabilityExportServiceTests
{
    private const string TimeZoneId = "Profitability/UTC-4";

    private static readonly TimeZoneInfo FixedTimeZone = TimeZoneInfo.CreateCustomTimeZone(
        TimeZoneId,
        TimeSpan.FromHours(-4),
        "Profitability UTC-4",
        "Profitability UTC-4");

    private static readonly DateTime FromUtc = new(2026, 11, 2, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime ToUtc = new(2026, 11, 3, 4, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime GeneratedAtUtc = new(2026, 11, 5, 15, 30, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ExportAsync_Csv_ProducesBomSemicolonMetadataSectionsAndPettyCashDetail()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        SeedPettyCash(context);
        await context.SaveChangesAsync();

        var summary = CreateSummary();
        var (service, profitability) = CreateService(context, summary);

        var file = await service.ExportAsync(
            CreateFilter(),
            "csv",
            CreateContext());

        profitability.Verify(
            s => s.GetSummaryAsync(It.IsAny<ProfitabilityFilterDto>(), It.IsAny<CancellationToken>()),
            Times.Once);

        Assert.Equal("text/csv; charset=utf-8", file.ContentType);
        Assert.Equal("Estado_Resultados_20261102_20261102.csv", file.FileName);

        // UTF-8 BOM so Excel opens the es-VE export with the right encoding.
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, file.Content.Take(3).ToArray());
        var text = Encoding.UTF8.GetString(file.Content, 3, file.Content.Length - 3);

        Assert.Contains("ESTADO DE RESULTADOS", text);
        Assert.Contains("Empresa;Comercializadora Demo", text);
        Assert.Contains("Generado (UTC);05/11/2026 15:30:00", text);
        Assert.Contains("Generado (hora local);05/11/2026 11:30:00", text);
        Assert.Contains("Exportado por;Ana Gerente", text);
        Assert.Contains($"Zona horaria;{TimeZoneId}", text);
        Assert.Contains("Moneda de reporte;USD ($)", text);
        Assert.Contains("Período (UTC);02/11/2026 04:00:00 a 03/11/2026 04:00:00", text);
        Assert.Contains("Filtros aplicados;Período: Custom (2026-11-02 - 2026-11-02)", text);
        Assert.Contains(';', text);

        Assert.Contains("RESUMEN DEVENGADO", text);
        Assert.Contains("RESUMEN PERCIBIDO", text);
        Assert.Contains("COBERTURA", text);
        Assert.Contains("PRODUCTOS Y CATEGORIAS", text);
        Assert.Contains("MEDIOS DE PAGO", text);
        Assert.Contains("CAJA CHICA POR CATEGORIA", text);

        // Known values from the contracted summary, formatted for the USD report currency.
        Assert.Contains("Ventas Netas;80.0000", text);
        Assert.Contains("Utilidad Neta;-11.6500", text);
        Assert.Contains("Cobros Totales;180.0000", text);
        Assert.Contains("Utilidad Bruta Percibida;46.0000", text);
        Assert.Contains("Cobertura de Costos (%);73.68", text);
        Assert.Contains("Cobertura de Comisiones (%);100.00", text);
        Assert.Contains("Líneas con Costo Desconocido;1", text);

        // Product/category rows: formula-injection escaping and currency formatting.
        Assert.Contains("10;'=SUM(A1);1;'=Categoria;2;100.0000;60.0000;40.0000;40.00", text);
        Assert.Contains("'+Impuesto", text);
        Assert.Contains("'-Descuento", text);
        Assert.Contains("'@Atajo", text);

        Assert.Contains("3;Punto de Venta;650.0000;4.5000;650.0000;0.0000;100.00", text);

        // Petty cash detail: only active expenses inside the summary window.
        Assert.Contains("Varios;1;10.0000;500.00;10.0000", text);
        Assert.Contains("InsumosLimpieza;1;5.2500;262.50;5.2500", text);
        Assert.DoesNotContain("FletesAcarreo", text);
        Assert.DoesNotContain("50.0000;2500.00", text);
    }

    [Fact]
    public async Task ExportAsync_Xlsx_ContainsFourTabsStyledHeadersAndPettyCashCategoryRows()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        SeedPettyCash(context);
        await context.SaveChangesAsync();

        var (service, _) = CreateService(context, CreateSummary());

        var file = await service.ExportAsync(CreateFilter(), "xlsx", CreateContext());

        Assert.Equal(
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            file.ContentType);
        Assert.Equal("Estado_Resultados_20261102_20261102.xlsx", file.FileName);

        using var stream = new MemoryStream(file.Content);
        using var workbook = new XLWorkbook(stream);

        Assert.Equal(
            new[] { "Resumen Ejecutivo", "Por Producto y Categoría", "Medios de Pago", "Caja Chica por Categoría" },
            workbook.Worksheets.Select(sheet => sheet.Name).ToArray());

        var executive = workbook.Worksheet("Resumen Ejecutivo");
        Assert.Equal("ESTADO DE RESULTADOS", executive.Cell(1, 1).GetString());
        Assert.Equal("Comercializadora Demo", ValueNextTo(executive, "Empresa:").GetString());
        Assert.Equal("Ana Gerente", ValueNextTo(executive, "Exportado por:").GetString());
        Assert.Contains("Período: Custom (2026-11-02 - 2026-11-02)", ValueNextTo(executive, "Filtros aplicados:").GetString());

        var executiveTexts = CellTexts(executive);
        Assert.Contains("RESUMEN DEVENGADO", executiveTexts);
        Assert.Contains("RESUMEN PERCIBIDO", executiveTexts);
        Assert.Contains("COBERTURA", executiveTexts);
        Assert.Equal(160m, ValueNextTo(executive, "Ventas Brutas").GetValue<decimal>());
        Assert.Equal(80m, ValueNextTo(executive, "Ventas Netas").GetValue<decimal>());
        Assert.Equal(-11.65m, ValueNextTo(executive, "Utilidad Neta").GetValue<decimal>());
        Assert.Equal(180m, ValueNextTo(executive, "Cobros Totales").GetValue<decimal>());
        Assert.Equal(73.6842m, ValueNextTo(executive, "Cobertura de Costos (%)").GetValue<decimal>());

        var header = FindCell(executive, "Concepto");
        Assert.True(header.Style.Font.Bold);
        Assert.Contains("%", ValueNextTo(executive, "Margen Bruto (%)").Style.NumberFormat.Format);

        var products = workbook.Worksheet("Por Producto y Categoría");
        var productTexts = CellTexts(products);
        Assert.Contains("=SUM(A1)", productTexts);
        Assert.Contains("=Categoria", productTexts);
        Assert.Contains("Producto", productTexts);

        var methods = workbook.Worksheet("Medios de Pago");
        Assert.Contains("Punto de Venta", CellTexts(methods));
        Assert.Equal(650m, ValueNextTo(methods, "Punto de Venta").GetValue<decimal>());

        var pettyCash = workbook.Worksheet("Caja Chica por Categoría");
        Assert.Contains("Varios", CellTexts(pettyCash));
        Assert.Contains("InsumosLimpieza", CellTexts(pettyCash));
        Assert.Equal(1, ValueNextTo(pettyCash, "Varios").GetValue<int>());
        Assert.Equal(10m, NextTo(pettyCash, "Varios", 2).GetValue<decimal>());
        Assert.Equal(500m, NextTo(pettyCash, "Varios", 3).GetValue<decimal>());
        Assert.Equal(10m, NextTo(pettyCash, "Varios", 4).GetValue<decimal>());
        Assert.DoesNotContain("FletesAcarreo", CellTexts(pettyCash));
    }

    [Fact]
    public async Task ExportAsync_SingleCurrency_UsesLocalAmountForPettyCashAndMoneyFormat()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        SeedPettyCash(context);
        await context.SaveChangesAsync();

        var (service, _) = CreateService(context, CreateSummary(singleCurrency: true));

        var file = await service.ExportAsync(CreateFilter(), "csv", CreateContext());
        var text = Encoding.UTF8.GetString(file.Content, 3, file.Content.Length - 3);

        Assert.Contains("Moneda de reporte;COP (COL$)", text);
        Assert.Contains("Ventas Netas;80.00", text);
        Assert.Contains("Varios;1;10.0000;500.00;500.00", text);
    }

    [Theory]
    [InlineData("CSV", "text/csv; charset=utf-8")]
    [InlineData(" XLSX ", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet")]
    public async Task ExportAsync_NormalizesFormatName(string format, string expectedContentType)
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var (service, _) = CreateService(context, CreateSummary());

        var file = await service.ExportAsync(CreateFilter(), format, CreateContext());

        Assert.Equal(expectedContentType, file.ContentType);
    }

    [Fact]
    public async Task ExportAsync_Pdf_ThrowsPendingMessageForF3Wu3()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var (service, _) = CreateService(context, CreateSummary());

        var exception = await Assert.ThrowsAsync<NotSupportedException>(
            () => service.ExportAsync(CreateFilter(), "pdf", CreateContext()));

        Assert.Equal("El formato PDF se habilita en F3-WU3.", exception.Message);
    }

    [Fact]
    public async Task ExportAsync_UnknownFormat_ThrowsArgumentException()
    {
        using var context = TestDatabaseFactory.CreateSalesDbContext();
        var (service, _) = CreateService(context, CreateSummary());

        await Assert.ThrowsAsync<ArgumentException>(
            () => service.ExportAsync(CreateFilter(), "docx", CreateContext()));
    }

    private static (ProfitabilityExportService Service, Mock<IProfitabilityService> Profitability) CreateService(
        SalesDbContext context,
        ProfitabilitySummaryDto summary)
    {
        var profitability = new Mock<IProfitabilityService>();
        profitability
            .Setup(service => service.GetSummaryAsync(
                It.IsAny<ProfitabilityFilterDto>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(summary);

        var service = new ProfitabilityExportService(
            profitability.Object,
            context,
            new StubTimeZoneProvider());
        return (service, profitability);
    }

    private static ProfitabilityExportContext CreateContext() => new()
    {
        CompanyName = "Comercializadora Demo",
        ExportedBy = "Ana Gerente",
        FilterSummary = "Período: Custom (2026-11-02 - 2026-11-02)",
        GeneratedAtUtc = GeneratedAtUtc
    };

    private static ProfitabilityFilterDto CreateFilter() => new()
    {
        Preset = ProfitabilityPeriodPreset.Custom,
        FromDate = new DateOnly(2026, 11, 2),
        ToDate = new DateOnly(2026, 11, 2)
    };

    private static ProfitabilitySummaryDto CreateSummary(bool singleCurrency = false) => new()
    {
        FromUtc = FromUtc,
        ToUtc = ToUtc,
        TimeZoneId = TimeZoneId,
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
        },
        Breakdown = new ProfitabilityBreakdownDto
        {
            Products = new List<ProductBreakdownDto>
            {
                new() { ProductId = 10, ProductName = "=SUM(A1)", CategoryId = 1, CategoryName = "=Categoria", QuantitySold = 2m, Revenue = 100m, Cogs = 60m, GrossProfit = 40m, GrossMarginPercent = 40m },
                new() { ProductId = 11, ProductName = "+Impuesto", CategoryId = 2, CategoryName = "+Categoria", QuantitySold = 1m, Revenue = 30m, Cogs = 20m, GrossProfit = 10m, GrossMarginPercent = 33.3333m },
                new() { ProductId = 12, ProductName = "-Descuento", CategoryId = 3, CategoryName = "-Categoria", QuantitySold = 1m, Revenue = 20m, Cogs = 10m, GrossProfit = 10m, GrossMarginPercent = 50m },
                new() { ProductId = 13, ProductName = "@Atajo", CategoryId = 4, CategoryName = "@Categoria", QuantitySold = 1m, Revenue = 10m, Cogs = 4m, GrossProfit = 6m, GrossMarginPercent = 60m }
            },
            Categories = new List<CategoryBreakdownDto>
            {
                new() { CategoryId = 1, CategoryName = "=Categoria", Revenue = 100m, Cogs = 60m, GrossProfit = 40m, GrossMarginPercent = 40m, ContributionPercent = 62.5m }
            },
            PaymentMethods = new List<PaymentMethodBreakdownDto>
            {
                new() { PaymentMethodId = 3, PaymentMethodName = "Punto de Venta", CollectedAmount = 650m, FeeAmount = 4.5m, KnownFeeAmount = 650m, UnknownFeeAmount = 0m, FeeCoveragePercent = 100m }
            }
        }
    };

    private static void SeedPettyCash(SalesDbContext context)
    {
        context.CashDrawerSessions.Add(new CashDrawerSession
        {
            Id = 1,
            OpenedAt = new DateTime(2026, 11, 2, 4, 0, 0, DateTimeKind.Utc),
            Status = CashDrawerStatus.Open,
            OpeningExchangeRate = 50m
        });

        context.PettyCashExpenses.AddRange(
            CreateExpense(1, PettyCashCategory.Varios, 10m, 500m,
                new DateTime(2026, 11, 2, 17, 0, 0, DateTimeKind.Utc)),
            CreateExpense(2, PettyCashCategory.InsumosLimpieza, 5.25m, 262.5m,
                new DateTime(2026, 11, 2, 18, 0, 0, DateTimeKind.Utc)),
            CreateExpense(3, PettyCashCategory.Varios, 50m, 2500m,
                new DateTime(2026, 11, 2, 19, 0, 0, DateTimeKind.Utc),
                PettyCashStatus.Cancelled,
                new DateTime(2026, 11, 2, 20, 0, 0, DateTimeKind.Utc)),
            CreateExpense(4, PettyCashCategory.FletesAcarreo, 7m, 350m,
                new DateTime(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc)));
    }

    private static PettyCashExpense CreateExpense(
        int id,
        PettyCashCategory category,
        decimal amountUsd,
        decimal amountBsS,
        DateTime createdAtUtc,
        PettyCashStatus status = PettyCashStatus.Active,
        DateTime? cancelledAtUtc = null) => new()
    {
        Id = id,
        VoucherNumber = id,
        SessionId = 1,
        Category = category,
        Currency = PettyCashCurrency.Usd,
        AmountUsd = amountUsd,
        AmountBsS = amountBsS,
        HistoricalRate = 50m,
        Concept = $"Test expense {id}",
        Beneficiary = "Test",
        ReferenceNumber = $"REF-{id}",
        CreatedByUserId = 1,
        CreatedAtUtc = createdAtUtc,
        Status = status,
        CancelledAtUtc = cancelledAtUtc
    };

    private static IReadOnlyList<string> CellTexts(IXLWorksheet worksheet) =>
        worksheet.CellsUsed()
            .Where(cell => cell.DataType == XLDataType.Text)
            .Select(cell => cell.GetString())
            .ToList();

    private static IXLCell FindCell(IXLWorksheet worksheet, string text) =>
        worksheet.CellsUsed().First(cell => cell.GetString() == text);

    private static IXLCell ValueNextTo(IXLWorksheet worksheet, string label) =>
        NextTo(worksheet, label, 1);

    private static IXLCell NextTo(IXLWorksheet worksheet, string label, int columnOffset)
    {
        var cell = FindCell(worksheet, label);
        return worksheet.Cell(cell.Address.RowNumber, cell.Address.ColumnNumber + columnOffset);
    }

    private sealed class StubTimeZoneProvider : ITimeZoneProvider
    {
        public Task<TimeZoneInfo> GetTimeZoneInfoAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(FixedTimeZone);

        public Task<string> GetTimeZoneIdAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(TimeZoneId);

        public void Invalidate()
        {
        }
    }
}
