using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class BcvScraperServiceTests
{
    private const string BcvUrl = "https://www.bcv.org.ve/";
    private const string DolarApiCurrentUrl = "https://ve.dolarapi.com/v1/dolares";
    private const string DolarApiOfficialHistoryUrl = "https://ve.dolarapi.com/v1/historicos/dolares/oficial";

    [Fact]
    public async Task GetOfficialUsdRateAsync_WhenBcvHtmlSucceeds_DoesNotCallDolarApi()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            Assert.Equal(BcvUrl, request.RequestUri!.ToString());
            return Task.FromResult(Html("<html><body><div id=\"dolar\"><strong>911,6243</strong></div></body></html>"));
        });

        var rate = await CreateService(handler, new DateOnly(2026, 11, 16)).GetOfficialUsdRateAsync();

        Assert.NotNull(rate);
        Assert.Equal(911.63m, rate.Value);
        Assert.Single(handler.RequestUris);
    }

    [Fact]
    public async Task GetOfficialUsdRateAsync_WhenBcvFails_UsesDolarApiCurrentOfficialOnWeekday()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            return uri switch
            {
                BcvUrl => Task.FromResult(Status(HttpStatusCode.BadGateway)),
                DolarApiCurrentUrl => Task.FromResult(Json(CurrentOfficialJson("911.6243", "2026-11-16T00:00:00-04:00"))),
                _ => throw new InvalidOperationException($"Unexpected request: {uri}")
            };
        });

        var rate = await CreateService(handler, new DateOnly(2026, 11, 16)).GetOfficialUsdRateAsync();

        Assert.NotNull(rate);
        Assert.Equal(911.63m, rate.Value);
        Assert.Contains(DolarApiCurrentUrl, handler.RequestUris);
        Assert.DoesNotContain(handler.RequestUris, uri => uri.Contains("/historicos/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetOfficialUsdRateAsync_WhenWeekend_UsesNextOfficialRateFromHistory_NotFriday()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            return uri switch
            {
                BcvUrl => Task.FromResult(Status(HttpStatusCode.BadGateway)),
                DolarApiCurrentUrl => Task.FromResult(Json(CurrentOfficialJson("911.62", "2026-11-13T00:00:00-04:00"))),
                DolarApiOfficialHistoryUrl => Task.FromResult(Json(OfficialHistoryJson(
                    ("2026-11-13", "911.62"),
                    ("2026-11-16", "914.02")))),
                _ => throw new InvalidOperationException($"Unexpected request: {uri}")
            };
        });

        var rate = await CreateService(handler, new DateOnly(2026, 11, 15)).GetOfficialUsdRateAsync();

        Assert.NotNull(rate);
        Assert.Equal(914.02m, rate.Value);
        Assert.Contains(DolarApiOfficialHistoryUrl, handler.RequestUris);
    }

    [Fact]
    public async Task GetOfficialUsdRateAsync_WhenWeekendAndNoNextOfficial_ReturnsNull_InsteadOfFriday()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            return uri switch
            {
                BcvUrl => Task.FromResult(Status(HttpStatusCode.BadGateway)),
                DolarApiCurrentUrl => Task.FromResult(Json(CurrentOfficialJson("911.62", "2026-11-13T00:00:00-04:00"))),
                DolarApiOfficialHistoryUrl => Task.FromResult(Json(OfficialHistoryJson(("2026-11-13", "911.62")))),
                _ => throw new InvalidOperationException($"Unexpected request: {uri}")
            };
        });

        var rate = await CreateService(handler, new DateOnly(2026, 11, 15)).GetOfficialUsdRateAsync();

        Assert.Null(rate);
    }

    [Fact]
    public async Task GetOfficialUsdRateAsync_WhenWeekendCurrentOfficialAlreadyHasNextRate_UsesIt()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            return uri switch
            {
                BcvUrl => Task.FromResult(Status(HttpStatusCode.BadGateway)),
                DolarApiCurrentUrl => Task.FromResult(Json(CurrentOfficialJson("914.02", "2026-11-16T00:00:00-04:00"))),
                _ => throw new InvalidOperationException($"Unexpected request: {uri}")
            };
        });

        var rate = await CreateService(handler, new DateOnly(2026, 11, 15)).GetOfficialUsdRateAsync();

        Assert.NotNull(rate);
        Assert.Equal(914.02m, rate.Value);
        Assert.DoesNotContain(DolarApiOfficialHistoryUrl, handler.RequestUris);
    }

    [Fact]
    public async Task GetOfficialUsdRateAsync_WhenWeekdayCurrentOfficialFails_UsesLatestHistoricalOfficial()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            return uri switch
            {
                BcvUrl => Task.FromResult(Status(HttpStatusCode.BadGateway)),
                DolarApiCurrentUrl => Task.FromResult(Status(HttpStatusCode.InternalServerError)),
                DolarApiOfficialHistoryUrl => Task.FromResult(Json(OfficialHistoryJson(
                    ("2026-11-13", "911.62"),
                    ("2026-11-16", "914.02"),
                    ("2026-11-17", "920.00")))),
                _ => throw new InvalidOperationException($"Unexpected request: {uri}")
            };
        });

        var rate = await CreateService(handler, new DateOnly(2026, 11, 16)).GetOfficialUsdRateAsync();

        Assert.NotNull(rate);
        Assert.Equal(914.02m, rate.Value);
    }

    [Fact]
    public async Task GetOfficialUsdRateAsync_WhenWeekdayCurrentOfficialRateIsInvalid_UsesHistorical()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            return uri switch
            {
                BcvUrl => Task.FromResult(Status(HttpStatusCode.BadGateway)),
                DolarApiCurrentUrl => Task.FromResult(Json(CurrentOfficialJson("0", "2026-11-16T00:00:00-04:00"))),
                DolarApiOfficialHistoryUrl => Task.FromResult(Json(OfficialHistoryJson(("2026-11-16", "914.02")))),
                _ => throw new InvalidOperationException($"Unexpected request: {uri}")
            };
        });

        var rate = await CreateService(handler, new DateOnly(2026, 11, 16)).GetOfficialUsdRateAsync();

        Assert.NotNull(rate);
        Assert.Equal(914.02m, rate.Value);
    }

    [Fact]
    public async Task GetOfficialUsdRateAsync_WhenAllOfficialSourcesFail_ReturnsNull()
    {
        var handler = new StubHttpMessageHandler((request, _) =>
        {
            var uri = request.RequestUri!.ToString();
            return uri switch
            {
                BcvUrl => Task.FromResult(Status(HttpStatusCode.BadGateway)),
                DolarApiCurrentUrl => Task.FromResult(Status(HttpStatusCode.InternalServerError)),
                DolarApiOfficialHistoryUrl => Task.FromResult(Status(HttpStatusCode.InternalServerError)),
                _ => throw new InvalidOperationException($"Unexpected request: {uri}")
            };
        });

        var rate = await CreateService(handler, new DateOnly(2026, 11, 16)).GetOfficialUsdRateAsync();

        Assert.Null(rate);
    }

    [Fact]
    public async Task GetOfficialUsdRateAsync_WhenDolarApiTimesOut_FallsBackWithPerProviderTimeout()
    {
        var handler = new StubHttpMessageHandler(async (request, cancellationToken) =>
        {
            var uri = request.RequestUri!.ToString();
            switch (uri)
            {
                case BcvUrl:
                    return Status(HttpStatusCode.BadGateway);
                case DolarApiCurrentUrl:
                    await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
                    return Json(CurrentOfficialJson("911.62", "2026-11-16T00:00:00-04:00"));
                case DolarApiOfficialHistoryUrl:
                    return Json(OfficialHistoryJson(("2026-11-16", "914.02")));
                default:
                    throw new InvalidOperationException($"Unexpected request: {uri}");
            }
        });

        var stopwatch = Stopwatch.StartNew();
        var rate = await CreateService(handler, new DateOnly(2026, 11, 16), fallbackTimeoutSeconds: 1)
            .GetOfficialUsdRateAsync();
        stopwatch.Stop();

        Assert.NotNull(rate);
        Assert.Equal(914.02m, rate.Value);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Fallback took too long: {stopwatch.Elapsed}.");
    }

    private static TestableBcvScraperService CreateService(
        StubHttpMessageHandler handler,
        DateOnly today,
        int fallbackTimeoutSeconds = 6)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BcvSettings:TimeoutSeconds"] = "1",
                ["BcvSettings:FallbackTimeoutSeconds"] = fallbackTimeoutSeconds.ToString(CultureInfo.InvariantCulture)
            })
            .Build();

        return new TestableBcvScraperService(
            new HttpClient(handler),
            NullLogger<BcvScraperService>.Instance,
            configuration,
            today);
    }

    private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(html, Encoding.UTF8, "text/html")
    };

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage Status(HttpStatusCode statusCode) => new(statusCode);

    private static string CurrentOfficialJson(string promedio, string fechaActualizacion) =>
        $$"""
        [
          {
            "moneda": "USD",
            "fuente": "oficial",
            "nombre": "Dólar",
            "compra": null,
            "venta": null,
            "promedio": {{promedio}},
            "fechaActualizacion": "{{fechaActualizacion}}"
          },
          {
            "moneda": "USD",
            "fuente": "paralelo",
            "nombre": "Paralelo",
            "compra": null,
            "venta": null,
            "promedio": 1074.089734,
            "fechaActualizacion": "2026-11-15T15:00:59.357Z"
          }
        ]
        """;

    private static string OfficialHistoryJson(params (string Date, string Rate)[] entries)
    {
        var builder = new StringBuilder("[");
        for (var i = 0; i < entries.Length; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(CultureInfo.InvariantCulture, $$"""
                {
                  "fuente": "oficial",
                  "compra": null,
                  "venta": null,
                  "promedio": {{entries[i].Rate}},
                  "fecha": "{{entries[i].Date}}"
                }
                """);
        }

        builder.Append(']');
        return builder.ToString();
    }

    private sealed class TestableBcvScraperService : BcvScraperService
    {
        private readonly DateOnly _today;

        public TestableBcvScraperService(
            HttpClient httpClient,
            Microsoft.Extensions.Logging.ILogger<BcvScraperService> logger,
            IConfiguration configuration,
            DateOnly today)
            : base(httpClient, logger, configuration)
        {
            _today = today;
        }

        protected override DateOnly GetTodayVenezuelaDate() => _today;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        public List<string> RequestUris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUris.Add(request.RequestUri!.ToString());
            return _handler(request, cancellationToken);
        }
    }
}
