using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.181-T2 (S5): contrato HTTP del cliente WPF de la edición masiva de precios. Fija las rutas
/// (preview/apply), la forma del cuerpo (enums numéricos, selección explícita o por filtro) y el
/// mapeo del resultado compartido congelado por T1.
/// </summary>
public class BulkPriceEditProductServiceTests
{
    private const string ResultJson = """
    {
      "exchangeRate": 50.0,
      "selectedCount": 2,
      "updatedCount": 1,
      "skippedCount": 1,
      "rows": [
        {
          "productId": 1,
          "sku": "A-1",
          "name": "Arroz",
          "isGroupHeader": false,
          "hasIndependentPricing": false,
          "skipReason": null,
          "isError": false,
          "changes": [
            { "field": 0, "beforeValue": 1.20, "afterValue": 1.50, "beforeBsS": 60.0, "afterBsS": 75.0 }
          ]
        },
        {
          "productId": 2,
          "sku": "B-2",
          "name": "Harina",
          "isGroupHeader": false,
          "hasIndependentPricing": false,
          "skipReason": "Sin cambios: los valores resultantes son iguales a los actuales.",
          "isError": false,
          "changes": []
        }
      ]
    }
    """;

    private static (ProductService Service, StubHandler Handler) CreateService(
        string body,
        HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHandler(body, status);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") };
        return (new ProductService(client), handler);
    }

    [Fact]
    public async Task PreviewBulkPricesAsync_PostsToPreviewRoute_WithNumericEnumsAndExplicitSelection()
    {
        var (service, handler) = CreateService(ResultJson);

        var result = await service.PreviewBulkPricesAsync(new BulkPriceUpdateRequestDto
        {
            ProductIds = new System.Collections.Generic.List<int> { 3, 1 },
            Operations = new System.Collections.Generic.List<BulkPriceOperationDto>
            {
                new() { Field = BulkPriceField.RetailPrice, Mode = BulkPriceMode.IncreasePercent, Value = 10m }
            }
        });

        Assert.Equal("/api/products/bulk-prices/preview", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var root = doc.RootElement;
        Assert.Equal(new[] { 3, 1 }, root.GetProperty("productIds").EnumerateArray().Select(e => e.GetInt32()).ToArray());
        Assert.False(root.GetProperty("selectAllFiltered").GetBoolean());
        var operation = root.GetProperty("operations")[0];
        Assert.Equal(0, operation.GetProperty("field").GetInt32());
        Assert.Equal(1, operation.GetProperty("mode").GetInt32());
        Assert.Equal(10m, operation.GetProperty("value").GetDecimal());

        Assert.Equal(50m, result.ExchangeRate);
        Assert.Equal(2, result.SelectedCount);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(1, result.SkippedCount);
        Assert.Equal(2, result.Rows.Count);

        var updatedRow = result.Rows.Single(r => r.ProductId == 1);
        Assert.Null(updatedRow.SkipReason);
        var change = Assert.Single(updatedRow.Changes);
        Assert.Equal(BulkPriceField.RetailPrice, change.Field);
        Assert.Equal(1.20m, change.BeforeValue);
        Assert.Equal(1.50m, change.AfterValue);
        Assert.Equal(60m, change.BeforeBsS);
        Assert.Equal(75m, change.AfterBsS);

        var skippedRow = result.Rows.Single(r => r.ProductId == 2);
        Assert.Contains("Sin cambios", skippedRow.SkipReason);
        Assert.Empty(skippedRow.Changes);
    }

    [Fact]
    public async Task ApplyBulkPricesAsync_PostsToApplyRoute_AndDeserializesResult()
    {
        var (service, handler) = CreateService(ResultJson);

        var result = await service.ApplyBulkPricesAsync(new BulkPriceUpdateRequestDto
        {
            ProductIds = new System.Collections.Generic.List<int> { 1, 2 },
            Operations = new System.Collections.Generic.List<BulkPriceOperationDto>
            {
                new() { Field = BulkPriceField.CostPrice, Mode = BulkPriceMode.SetFixed, Value = 0.90m }
            }
        });

        Assert.Equal("/api/products/bulk-prices/apply", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(2, result.Rows.Count);
    }

    [Fact]
    public async Task PreviewBulkPricesAsync_WithFilterSelection_SendsSelectAllFilteredAndFilter()
    {
        var (service, handler) = CreateService(ResultJson);

        await service.PreviewBulkPricesAsync(new BulkPriceUpdateRequestDto
        {
            Filter = new BulkPriceSelectionFilterDto
            {
                SearchText = "arroz",
                StatusFilter = "active",
                CategoryId = 4
            },
            SelectAllFiltered = true,
            Operations = new System.Collections.Generic.List<BulkPriceOperationDto>
            {
                new() { Field = BulkPriceField.RetailPrice, Mode = BulkPriceMode.IncreasePercent, Value = 5m }
            }
        });

        using var doc = JsonDocument.Parse(handler.LastRequestBody!);
        var root = doc.RootElement;
        Assert.True(root.GetProperty("selectAllFiltered").GetBoolean());
        Assert.True(
            !root.TryGetProperty("productIds", out var productIds) || productIds.ValueKind == JsonValueKind.Null,
            "El modo filtro no debe enviar ProductIds.");
        var filter = root.GetProperty("filter");
        Assert.Equal("arroz", filter.GetProperty("searchText").GetString());
        Assert.Equal("active", filter.GetProperty("statusFilter").GetString());
        Assert.Equal(4, filter.GetProperty("categoryId").GetInt32());
    }

    [Fact]
    public async Task ApplyBulkPricesAsync_On409_ThrowsHttpRequestExceptionWithConflictStatusAndServerDetail()
    {
        const string conflictBody = """
        {
          "type": "https://tools.ietf.org/html/rfc9110#section-15.5.10",
          "title": "Conflict",
          "status": 409,
          "detail": "No se aplicó la actualización masiva: los precios fueron modificados por otro usuario. Vuelva a cargar el catálogo e intente nuevamente."
        }
        """;
        var (service, _) = CreateService(conflictBody, HttpStatusCode.Conflict);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => service.ApplyBulkPricesAsync(new BulkPriceUpdateRequestDto
        {
            ProductIds = new System.Collections.Generic.List<int> { 1 },
            Operations = new System.Collections.Generic.List<BulkPriceOperationDto>
            {
                new() { Field = BulkPriceField.RetailPrice, Mode = BulkPriceMode.SetFixed, Value = 2m }
            }
        }));

        Assert.Equal(HttpStatusCode.Conflict, ex.StatusCode);
        Assert.Contains("modificados por otro usuario", ex.Message);
    }

    [Fact]
    public async Task PreviewBulkPricesAsync_On400_ThrowsWithServerDetail()
    {
        const string badRequestBody = """
        {
          "title": "Bad Request",
          "status": 400,
          "detail": "La selección excede el máximo permitido (1000 productos)."
        }
        """;
        var (service, _) = CreateService(badRequestBody, HttpStatusCode.BadRequest);

        var ex = await Assert.ThrowsAsync<Exception>(() => service.PreviewBulkPricesAsync(new BulkPriceUpdateRequestDto
        {
            ProductIds = new System.Collections.Generic.List<int> { 1 },
            Operations = new System.Collections.Generic.List<BulkPriceOperationDto>
            {
                new() { Field = BulkPriceField.RetailPrice, Mode = BulkPriceMode.SetFixed, Value = 2m }
            }
        }));

        Assert.Contains("máximo permitido", ex.Message);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly HttpStatusCode _status;

        public StubHandler(string body, HttpStatusCode status)
        {
            _body = body;
            _status = status;
        }

        public HttpRequestMessage? LastRequest { get; private set; }

        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastRequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            };
        }
    }
}
