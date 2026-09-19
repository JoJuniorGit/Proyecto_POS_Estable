using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class AuthClientContractTests
{
    [Fact]
    public async Task WpfLoginClient_When403WithRequiresPasswordChange_ReturnsFlagAndMessage()
    {
        const string body = "{\"type\":\"https://httpstatuses.com/403\",\"title\":\"Forbidden\",\"status\":403,\"detail\":\"Debe cambiar su contraseña antes de continuar.\",\"message\":\"Debe cambiar su contraseña antes de continuar.\",\"requiresPasswordChange\":true,\"traceId\":\"test\"}";

        var httpClient = new HttpClient(new StaticResponseHandler(HttpStatusCode.Forbidden, body))
        {
            BaseAddress = new Uri("https://localhost:5001/")
        };

        var service = new Desktop.Client.Services.UserService(httpClient);

        var result = await service.LoginAsync("V-50000001", "TempPass#1234");

        Assert.NotNull(result);
        Assert.True(result!.RequiresPasswordChange);
        Assert.Equal("Debe cambiar su contraseña antes de continuar.", result.Message);
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _body;

        public StaticResponseHandler(HttpStatusCode statusCode, string body)
        {
            _statusCode = statusCode;
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json")
            });
        }
    }
}
