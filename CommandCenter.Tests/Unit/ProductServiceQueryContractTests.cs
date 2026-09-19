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
