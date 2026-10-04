using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading;
using System.Threading.Tasks;
using CommandCenter.Tests.Builders;
using Core.DTOs;
using Core.Interfaces;
using Inventory.Module.Data;
using Inventory.Module.Services.Ocr;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using OpenCvSharp;
using Xunit;

namespace CommandCenter.Tests.Integration;

/// <summary>
/// 8.147-T4/D12: contrato HTTP del endpoint POST /api/supplier-invoices/ocr-extract con el
/// pipeline real de MVC + DI (motor OCR stub, sin nativos de Tesseract): happy path con filas y
/// previews, RBAC Cashier 403, tipo/tamaño rechazados con ProblemDetails y cero persistencia.
/// </summary>
[Collection(PostgresRealCollection.Name)]
public class OcrExtractionEndpointTests
{
    private const string Route = "/api/supplier-invoices/ocr-extract";

    [Fact]
    public async Task OcrExtract_AdminUploadsImage_ReturnsLinesPreviewsAndPersistsNothing()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        await using var connectionScope = connection;
        await using var contextScope = context;
        var engine = new StubOcrEngine();
        await using var application = await CreateApplicationAsync(context, connection, engine);
        using var client = application.GetTestServer().CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "Admin");

        using var response = await PostFileAsync(client, "factura.jpg", CreateJpeg());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<OcrExtractionResultDto>();
        Assert.NotNull(result);
        var line = Assert.Single(result.Lines);
        Assert.Equal("Café", line.Name);
        Assert.Equal(2m, line.Quantity);
        Assert.Equal(5.50m, line.UnitCost);
        Assert.Equal(95.0m, line.QuantityConfidence);
        Assert.Equal("J-12345678-9", result.DetectedRif);
        Assert.Equal("COMERCIAL ESQUINA", result.DetectedSupplierName);

        var preview = Assert.Single(result.PreviewPagesBase64);
        using var decoded = Cv2.ImDecode(Convert.FromBase64String(preview), ImreadModes.Unchanged);
        Assert.False(decoded.Empty(), "la respuesta debe incluir una preview PNG decodificable");
        Assert.Equal(1, engine.Calls);

        // La extracción no persiste: la BD de la aplicación queda intacta.
        await using var verificationContext = CreateSqliteInventoryDbContext(connection);
        Assert.Equal(0, await verificationContext.SupplierInvoices.CountAsync());
        Assert.Equal(0, await verificationContext.SupplierInvoiceLines.CountAsync());
    }

    [Fact]
    public async Task OcrExtract_Cashier_ReturnsForbiddenWithoutProcessing()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        await using var connectionScope = connection;
        await using var contextScope = context;
        var engine = new StubOcrEngine();
        await using var application = await CreateApplicationAsync(context, connection, engine);
        using var client = application.GetTestServer().CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "Cashier");

        using var response = await PostFileAsync(client, "factura.png", CreateJpeg());

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task OcrExtract_UnsupportedType_ReturnsBadRequestProblemDetails()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        await using var connectionScope = connection;
        await using var contextScope = context;
        var engine = new StubOcrEngine();
        await using var application = await CreateApplicationAsync(context, connection, engine);
        using var client = application.GetTestServer().CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "Admin");

        using var response = await PostFileAsync(client, "factura.docx", new byte[] { 1, 2, 3 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains("no está soportada", problem.Detail, StringComparison.Ordinal);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task OcrExtract_OversizedFile_ReturnsBadRequestProblemDetails()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        await using var connectionScope = connection;
        await using var contextScope = context;
        var engine = new StubOcrEngine();
        await using var application = await CreateApplicationAsync(context, connection, engine);
        using var client = application.GetTestServer().CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "Admin");
        var oversized = new byte[(int)OcrExtractionService.MaxFileBytes + 1];

        using var response = await PostFileAsync(client, "factura.png", oversized);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains("20 MB", problem.Detail, StringComparison.Ordinal);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task OcrExtract_EmptyFile_ReturnsBadRequestProblemDetails()
    {
        var (context, connection) = TestDatabaseFactory.CreateSqliteInventoryDbContext();
        await using var connectionScope = connection;
        await using var contextScope = context;
        var engine = new StubOcrEngine();
        await using var application = await CreateApplicationAsync(context, connection, engine);
        using var client = application.GetTestServer().CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Role", "Admin");

        using var response = await PostFileAsync(client, "factura.png", Array.Empty<byte>());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();
        Assert.NotNull(problem);
        Assert.Contains("vacío", problem.Detail, StringComparison.Ordinal);
        Assert.Equal(0, engine.Calls);
    }

    private static async Task<HttpResponseMessage> PostFileAsync(HttpClient client, string fileName, byte[] bytes)
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(bytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(fileContent, "file", fileName);
        return await client.PostAsync(Route, content);
    }

    private static byte[] CreateJpeg(int width = 120, int height = 60)
    {
        using var image = new Mat(height, width, MatType.CV_8UC3, Scalar.White);
        Cv2.Rectangle(image, new Rect(10, 10, width / 2, height / 3), Scalar.Black, thickness: -1);
        Assert.True(Cv2.ImEncode(".jpg", image, out var jpeg));
        return jpeg;
    }

    private static InventoryDbContext CreateSqliteInventoryDbContext(SqliteConnection connection) => new(
        new DbContextOptionsBuilder<InventoryDbContext>()
            .UseSqlite(connection)
            .Options);

    private static async Task<WebApplication> CreateApplicationAsync(
        InventoryDbContext context,
        SqliteConnection connection,
        StubOcrEngine engine)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(Backend.API.Controllers.SupplierInvoicesController).Assembly);
        builder.Services.AddAuthentication("TestAuth")
            .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("TestAuth", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton(Mock.Of<ISupplierInvoiceService>());
        builder.Services.AddSingleton(context);
        builder.Services.AddSingleton(connection);

        // Mismo grafo de DI que producción, con IOcrEngine reemplazado por el stub determinista.
        builder.Services.AddSingleton<ImagePreprocessor>();
        builder.Services.AddSingleton<ImagePageDecoder>();
        builder.Services.AddSingleton<PdfPageDecoder>();
        builder.Services.AddSingleton<DocumentPageDecoder>();
        builder.Services.AddSingleton<InvoiceTableParser>();
        builder.Services.AddSingleton<IOcrEngine>(engine);
        builder.Services.AddScoped<IOcrExtractionService, OcrExtractionService>();

        var application = builder.Build();
        application.UseAuthentication();
        application.UseAuthorization();
        application.MapControllers();
        await application.StartAsync();
        return application;
    }

    private sealed class StubOcrEngine : IOcrEngine
    {
        private int _calls;

        public int Calls => _calls;

        public Task<OcrPage> RecognizeAsync(OcrImage image, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(image);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new OcrPage(new[]
            {
                new OcrWord("COMERCIAL", 20, 5, 40, 10, 90),
                new OcrWord("ESQUINA", 160, 5, 40, 10, 90),
                new OcrWord("J-12345678-9", 320, 5, 40, 10, 90),
                new OcrWord("Descripción", 40, 100, 40, 10, 90),
                new OcrWord("Cantidad", 200, 100, 40, 10, 90),
                new OcrWord("Precio", 340, 100, 40, 10, 90),
                new OcrWord("Café", 40, 130, 40, 10, 88),
                new OcrWord("2", 200, 130, 40, 10, 95),
                new OcrWord("5,50", 340, 130, 40, 10, 70)
            }));
        }
    }

    private sealed class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var role = Request.Headers["X-Test-Role"].ToString();
            if (string.IsNullOrWhiteSpace(role))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "ocr-extraction-test-user"),
                new Claim(ClaimTypes.Role, role)
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
            var ticket = new AuthenticationTicket(principal, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
