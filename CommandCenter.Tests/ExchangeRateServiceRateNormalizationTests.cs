using Desktop.Client.Services;
using System;
using System.Net.Http;
using Xunit;

namespace CommandCenter.Tests;

public class ExchangeRateServiceRateNormalizationTests
{
    private static ExchangeRateService CreateService()
    {
        using var httpClient = new HttpClient { BaseAddress = new Uri("http://localhost:5000/") };
        return new ExchangeRateService(httpClient);
    }

    [Fact]
    public void CurrentRate_WhenRateExact_RoundsUpToCeiling2Decimals()
    {
        var service = CreateService();
        try
        {
            service.CurrentRate = 36.502175m;
            Assert.Equal(36.51m, service.CurrentRate);
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public void SetCurrentRateSynchronously_WhenRateExact_RoundsUpToCeiling2Decimals()
    {
        var service = CreateService();
        try
        {
            service.SetCurrentRateSynchronously(36.502175m);
            Assert.Equal(36.51m, service.CurrentRate);
        }
        finally
        {
            service.Dispose();
        }
    }

    [Fact]
    public void SetCurrentRateSynchronously_WhenRateAlreadyRounded_IsIdempotent()
    {
        var service = CreateService();
        try
        {
            service.SetCurrentRateSynchronously(36.51m);
            Assert.Equal(36.51m, service.CurrentRate);
            service.SetCurrentRateSynchronously(36.51m);
            Assert.Equal(36.51m, service.CurrentRate);
        }
        finally
        {
            service.Dispose();
        }
    }
}