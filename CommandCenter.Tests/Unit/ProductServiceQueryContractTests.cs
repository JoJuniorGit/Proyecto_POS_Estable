using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Controllers;
using Desktop.Client.Services;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace CommandCenter.Tests.Unit;

/// <summary>
/// Guarda de costura cliente/servidor: los nombres de query que el cliente WPF envia deben ser
/// bindeables por los parametros [FromQuery] del controlador. Nace del bug 8.142 donde el cliente
/// mandaba "status" y el backend bindeaba "statusFilter": el filtro de estado se ignoraba en
/// silencio, sin error, sin test rojo y sin warning.
/// </summary>
public class ProductServiceQueryContractTests
{
    [Fact]
    public async Task GetPagedAsync_SendsOnlyQueryParametersThatTheBackendBinds()
    {
        Uri? capturedUri = null;
        var client = new HttpClient(new CapturingHandler(uri => capturedUri = uri))
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };
        var service = new ProductService(client);

        await service.GetPagedAsync("arroz", 2, 25, statusFilter: "active", sortBy: "name", isDescending: true);

        Assert.NotNull(capturedUri);

        var boundByController = typeof(ProductsController)
            .GetMethod(nameof(ProductsController.GetAllAsync))!
            .GetParameters()
            .Select(p => p.GetCustomAttribute<FromQueryAttribute>()?.Name ?? (p.GetCustomAttribute<FromQueryAttribute>() != null ? p.Name : null))
            .Where(name => name != null)
            .Select(name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sentKeys = capturedUri!.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=')[0])
            .ToList();

        Assert.NotEmpty(sentKeys);
        Assert.All(sentKeys, key => Assert.True(
            boundByController.Contains(key),
            $"El cliente envia '{key}', que el backend NO bindea (validos: {string.Join(", ", boundByController)})."));
    }

    // Guarda la costura de forma de respuesta: si el T del cliente deja de coincidir con el DTO paginado del backend, la deserializacion queda vacia sin lanzar error.
    [Fact]
    public async Task GetPagedAsync_DeserializesCamelCasePagedPayloadIntoItemsAndTotalCount()
    {
        const string payload = "{\"items\":[{\"id\":1,\"name\":\"Arroz\",\"sku\":\"SKU1\",\"priceBsS\":12.34}],\"totalCount\":1,\"hasMore\":false}";
        var client = new HttpClient(new JsonResponseHandler(payload))
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };
        var service = new ProductService(client);

        var result = await service.GetPagedAsync(null, 1, 50);

        var item = Assert.Single(result.Items);
        Assert.Equal(1, item.Id);
        Assert.Equal("Arroz", item.Name);
        Assert.Equal("SKU1", item.SKU);
        Assert.Equal(12.34m, item.PriceBsS);
        Assert.Equal(1, result.TotalCount);
    }

    private sealed class JsonResponseHandler : HttpMessageHandler
    {
        private readonly string _payload;

        public JsonResponseHandler(string payload) => _payload = payload;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_payload, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Action<Uri> _onRequest;

        public CapturingHandler(Action<Uri> onRequest) => _onRequest = onRequest;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _onRequest(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"items\":[],\"totalCount\":0,\"hasMore\":false}", Encoding.UTF8, "application/json")
            });
        }
    }
}
