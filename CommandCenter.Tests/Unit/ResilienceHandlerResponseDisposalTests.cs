using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Desktop.Client.Services;
using Moq;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class ResilienceHandlerResponseDisposalTests
{
    private sealed class TrackingResponse : HttpResponseMessage
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class SequencedHandler : HttpMessageHandler
    {
        private readonly Func<int, HttpResponseMessage> _responseFactory;
        private int _invocationCount;

        public SequencedHandler(Func<int, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public int InvocationCount => _invocationCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var count = Interlocked.Increment(ref _invocationCount);
            return Task.FromResult(_responseFactory(count));
        }
    }

    [Fact]
    public async Task SendAsync_When5xxThenSuccess_DisposesIntermediateResponseAndReturnsFinalIntact()
    {
        var intermediate = new TrackingResponse
        {
            StatusCode = HttpStatusCode.ServiceUnavailable,
            Content = new StringContent("{\"error\":\"transient\"}")
        };
        var final = new TrackingResponse
        {
            StatusCode = HttpStatusCode.OK,
            Content = new StringContent("{\"ok\":true}")
        };

        var innerHandler = new SequencedHandler(count => count == 1 ? intermediate : final);
        var healthPolling = new Mock<IHealthPollingService>();
        var handler = new ResilienceHandler(healthPolling.Object)
        {
            InnerHandler = innerHandler
        };

        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:5000/")
        };

        using var response = await client.GetAsync("api/products");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, innerHandler.InvocationCount);
        Assert.True(intermediate.IsDisposed);
        Assert.False(final.IsDisposed);
        Assert.Equal("{\"ok\":true}", await response.Content.ReadAsStringAsync());
        healthPolling.Verify(h => h.StartPolling(), Times.Never);
    }
}
