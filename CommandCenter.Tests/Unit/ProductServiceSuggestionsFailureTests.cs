using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Client.Services;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ProductServiceSuggestionsFailureTests
{
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;

        public FakeHandler(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, System.Text.Encoding.UTF8, "application/json"),
                RequestMessage = request
            });
        }
    }

    [Fact]
    public async Task GetSuggestionsAsync_WhenServerReturnsEmptyList_ReturnsEmptyList()
    {
        var service = CreateService(HttpStatusCode.OK, "[]");

        var result = await service.GetSuggestionsAsync("UNKNOWN-CODE", true, CancellationToken.None);

        Assert.Empty(result);
    }

    [Fact]
    public async Task GetSuggestionsAsync_WhenServerFails_PropagatesInsteadOfReturningEmpty()
    {
        var service = CreateService(HttpStatusCode.InternalServerError, "{\"message\":\"boom\"}");

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.GetSuggestionsAsync("UNKNOWN-CODE", true, CancellationToken.None));
    }

    [Fact]
    public async Task GetSuggestionsAsync_WhenServerUnreachable_PropagatesInsteadOfReturningEmpty()
    {
        var client = new HttpClient(new ThrowingHandler())
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };
        var service = new ProductService(client);

        await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.GetSuggestionsAsync("UNKNOWN-CODE", true, CancellationToken.None));
    }

    private static ProductService CreateService(HttpStatusCode statusCode, string body)
    {
        var client = new HttpClient(new FakeHandler(statusCode, body))
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };
        return new ProductService(client);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("Connection refused");
        }
    }
}
