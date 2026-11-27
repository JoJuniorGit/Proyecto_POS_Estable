using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using ClosedXML.Excel;
using Core.Entities;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// Paso 14 (F3-WU4, F3-S4): contrato HTTP de la exportación unificada del Estado de Resultados.
/// Corre contra el pipeline real y una base PostgreSQL aislada sin ventas, de modo que los tres
/// formatos deben renderizar reportes en cero sin fallar. Cubre RBAC, validación de formato,
/// Content-Type/Content-Disposition y el contenido real de CSV (BOM), XLSX (pestañas) y PDF.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection(PostgresRealCollection.Name)]
public class ProfitabilityExportHttpTests
{
    private const string CustomRange = "preset=Custom&fromDate=2026-11-01&toDate=2026-11-02";

    private static readonly (string Format, string MediaType, string Extension)[] Formats =
    {
        ("csv", "text/csv", ".csv"),
        ("xlsx", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", ".xlsx"),
        ("pdf", "application/pdf", ".pdf")
    };

    [Fact]
    public async Task Export_EnforcesRoleMatrixAndRejectsInvalidFormats()
    {
        await using var app = await AuthorizationEndToEndApplication.CreateAsync("profitability-export-rbac");
        var admin = await app.CreateUserAsync(9301, UserRole.Admin, "Admin Exportacion");
        var manager = await app.CreateUserAsync(9302, UserRole.Manager, "Manager Exportacion");
        var cashier = await app.CreateUserAsync(9303, UserRole.Cashier, "Cajero Exportacion");

        using var adminClient = app.CreateHttpClient(app.IssueToken(admin));
        using var managerClient = app.CreateHttpClient(app.IssueToken(manager));
        using var cashierClient = app.CreateHttpClient(app.IssueToken(cashier));
        using var anonymous = app.Factory.CreateClient();
        anonymous.DefaultRequestHeaders.Add("X-Client-Version", AuthorizationEndToEndApplication.ClientVersion);

        foreach (var (format, mediaType, extension) in Formats)
        {
            foreach (var client in new[] { adminClient, managerClient })
            {
                using var response = await client.GetAsync(
                    $"/api/profitability/export?format={format}&{CustomRange}");

                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal(mediaType, response.Content.Headers.ContentType?.MediaType);

                var disposition = response.Content.Headers.ContentDisposition;
                Assert.NotNull(disposition);
                Assert.Contains(extension, disposition!.ToString());

                var content = await response.Content.ReadAsByteArrayAsync();
                Assert.NotEmpty(content);

                switch (format)
                {
                    case "csv":
                        AssertCsvContract(content);
                        break;
                    case "xlsx":
                        using (var workbook = new XLWorkbook(new MemoryStream(content)))
                        {
                            Assert.Equal(4, workbook.Worksheets.Count);
                        }

                        break;
                    default:
                        AssertPdfContract(content);
                        break;
                }
            }
        }

        using var invalidResponse = await adminClient.GetAsync(
            $"/api/profitability/export?format=docx&{CustomRange}");
        Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);

        using var cashierResponse = await cashierClient.GetAsync(
            $"/api/profitability/export?format=csv&{CustomRange}");
        Assert.Equal(HttpStatusCode.Forbidden, cashierResponse.StatusCode);

        using var anonymousResponse = await anonymous.GetAsync(
            $"/api/profitability/export?format=csv&{CustomRange}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousResponse.StatusCode);
    }

    [Fact]
    public async Task Export_RendersCsvBomXlsxTabsAndNativePdf()
    {
        await using var app = await AuthorizationEndToEndApplication.CreateAsync("profitability-export-content");
        var admin = await app.CreateUserAsync(9311, UserRole.Admin, "Admin Contenido");
        using var adminClient = app.CreateHttpClient(app.IssueToken(admin));

        using var csvResponse = await adminClient.GetAsync(
            $"/api/profitability/export?format=csv&{CustomRange}");
        Assert.Equal(HttpStatusCode.OK, csvResponse.StatusCode);
        var csvBytes = await csvResponse.Content.ReadAsByteArrayAsync();
        var csvText = AssertCsvContract(csvBytes);
        // The isolated database has no sales, so the report must render zero data, not fail.
        Assert.Contains("Ventas Brutas;0.0000", csvText);

        using var xlsxResponse = await adminClient.GetAsync(
            $"/api/profitability/export?format=xlsx&{CustomRange}");
        Assert.Equal(HttpStatusCode.OK, xlsxResponse.StatusCode);
        var xlsxBytes = await xlsxResponse.Content.ReadAsByteArrayAsync();
        using var workbook = new XLWorkbook(new MemoryStream(xlsxBytes));
        Assert.Equal(
            new[] { "Resumen Ejecutivo", "Por Producto y Categoría", "Medios de Pago", "Caja Chica por Categoría" },
            workbook.Worksheets.Select(sheet => sheet.Name).ToArray());

        using var pdfResponse = await adminClient.GetAsync(
            $"/api/profitability/export?format=pdf&{CustomRange}");
        Assert.Equal(HttpStatusCode.OK, pdfResponse.StatusCode);
        var pdfBytes = await pdfResponse.Content.ReadAsByteArrayAsync();
        AssertPdfContract(pdfBytes);
    }

    private static string AssertCsvContract(byte[] content)
    {
        Assert.True(content.Length > 3);
        // UTF-8 BOM so Excel opens the es-VE export with the right encoding.
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, content.Take(3).ToArray());
        var text = Encoding.UTF8.GetString(content, 3, content.Length - 3);
        Assert.Contains(';', text);
        Assert.Contains("RESUMEN DEVENGADO", text);
        Assert.Contains("CAJA CHICA POR CATEGORIA", text);
        return text;
    }

    private static void AssertPdfContract(byte[] content)
    {
        var text = Encoding.ASCII.GetString(content);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.Contains("%%EOF", text);
    }
}
