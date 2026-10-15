using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// 8.179-T2 (S3): contrato HTTP del cliente de categorías de escritorio (rutas, verbos, JSON y
/// mapeo de errores ProblemDetails).
/// </summary>
public class CategoryClientServiceTests
{
    [Fact]
    public async Task GetCategoriesAsync_SendsGetToCategoriesRoute_AndDeserializesTreePayload()
    {
        var handler = new StubHttpMessageHandler(JsonResponse(
            """[{"id":1,"name":"Alimentos","parentCategoryId":null,"depth":0,"path":"Alimentos"},{"id":2,"name":"Lácteos","parentCategoryId":1,"depth":1,"path":"Alimentos > Lácteos"}]"""));
        var service = CreateService(handler);

        var categories = await service.GetCategoriesAsync();

        Assert.Equal(2, categories.Count);
        Assert.Equal("Alimentos > Lácteos", categories[1].Path);
        Assert.Equal(1, categories[1].Depth);
        Assert.Equal(1, categories[1].ParentCategoryId);
        Assert.Equal("GET", handler.RequestMethods[0]);
        Assert.Equal("/api/categories", handler.RequestPaths[0]);
    }

    [Fact]
    public async Task CreateAsync_SendsPostWithNameAndParent()
    {
        var handler = new StubHttpMessageHandler(JsonResponse(
            """{"id":9,"name":"Lácteos","parentCategoryId":3,"depth":1,"path":"Alimentos > Lácteos"}"""));
        var service = CreateService(handler);

        var created = await service.CreateAsync(new CreateCategoryRequestDto { Name = "Lácteos", ParentCategoryId = 3 });

        Assert.Equal(9, created.Id);
        Assert.Equal("POST", handler.RequestMethods[0]);
        Assert.Equal("/api/categories", handler.RequestPaths[0]);
        using var body = System.Text.Json.JsonDocument.Parse(handler.RequestBodies[0]);
        Assert.Equal("Lácteos", body.RootElement.GetProperty("name").GetString());
        Assert.Equal(3, body.RootElement.GetProperty("parentCategoryId").GetInt32());
    }

    [Fact]
    public async Task UpdateAsync_SendsPutToIdRouteWithFullReplacementBody()
    {
        var handler = new StubHttpMessageHandler(JsonResponse(
            """{"id":7,"name":"Bebidas sin alcohol","parentCategoryId":null,"depth":0,"path":"Bebidas sin alcohol"}"""));
        var service = CreateService(handler);

        var updated = await service.UpdateAsync(7, new UpdateCategoryRequestDto { Name = "Bebidas sin alcohol", ParentCategoryId = null });

        Assert.Equal("Bebidas sin alcohol", updated.Name);
        Assert.Equal("PUT", handler.RequestMethods[0]);
        Assert.Equal("/api/categories/7", handler.RequestPaths[0]);
        Assert.Contains("\"name\":\"Bebidas sin alcohol\"", handler.RequestBodies[0]);
        Assert.Contains("\"parentCategoryId\":null", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task DeleteAsync_SendsDeleteToIdRoute()
    {
        var handler = new StubHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.NoContent));
        var service = CreateService(handler);

        await service.DeleteAsync(5);

        Assert.Equal("DELETE", handler.RequestMethods[0]);
        Assert.Equal("/api/categories/5", handler.RequestPaths[0]);
    }

    [Fact]
    public async Task CreateAsync_WhenServerRejectsWithConflict_ThrowsReadableProblemDetailsMessage()
    {
        var handler = new StubHttpMessageHandler(JsonResponse(
            """{"title":"Conflicto","detail":"Ya existe una categoría con ese nombre en el mismo nivel."}""",
            HttpStatusCode.Conflict));
        var service = CreateService(handler);

        var ex = await Assert.ThrowsAsync<Exception>(() =>
            service.CreateAsync(new CreateCategoryRequestDto { Name = "Bebidas" }));

        Assert.Contains("Ya existe una categoría con ese nombre en el mismo nivel.", ex.Message);
    }

    [Fact]
    public async Task DeleteAsync_WhenServerRejectsWithConflict_ThrowsReadableMessage()
    {
        var handler = new StubHttpMessageHandler(JsonResponse(
            """{"detail":"No se puede eliminar la categoría porque tiene productos asignados."}""",
            HttpStatusCode.Conflict));
        var service = CreateService(handler);

        var ex = await Assert.ThrowsAsync<Exception>(() => service.DeleteAsync(1));

        Assert.Contains("productos asignados", ex.Message);
    }

    private static CategoryService CreateService(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:5000/") });

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class StubHttpMessageHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<string> RequestPaths { get; } = new();
        public List<string> RequestMethods { get; } = new();
        public List<string> RequestBodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPaths.Add(request.RequestUri?.PathAndQuery ?? string.Empty);
            RequestMethods.Add(request.Method.Method);
            RequestBodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));

            return _responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
