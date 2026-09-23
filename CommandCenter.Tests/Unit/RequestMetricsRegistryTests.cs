using System.Linq;
using Backend.API.Metrics;
using Xunit;

namespace CommandCenter.Tests.Unit;

public class RequestMetricsRegistryTests
{
    [Fact]
    public void Record_MultipleRequests_ComputesCountsAndErrorCount()
    {
        var registry = new RequestMetricsRegistry();

        registry.Record("POST", "/api/sales/1/complete", 100, 200);
        registry.Record("POST", "/api/sales/1/complete", 200, 200);
        registry.Record("POST", "/api/sales/1/complete", 50, 500);

        var snapshots = registry.GetSnapshot();
        var complete = Assert.Single(snapshots, s => s.Endpoint == "POST /api/sales/1/complete");

        Assert.Equal(3, complete.TotalCount);
        Assert.Equal(1, complete.ErrorCount);
        Assert.True(complete.MaxMilliseconds >= 200);
        Assert.True(complete.AverageMilliseconds > 0);
    }

    [Fact]
    public void Record_Samples_ComputesP95WithinExpectedRange()
    {
        var registry = new RequestMetricsRegistry();

        for (var i = 0; i < 100; i++)
        {
            var latency = i < 95 ? 100 : 4000;
            registry.Record("GET", "/api/health", latency, 200);
        }

        var snapshot = Assert.Single(registry.GetSnapshot(), s => s.Endpoint == "GET /api/health");
        Assert.True(snapshot.P95Milliseconds <= 4000);
        Assert.True(snapshot.P95Milliseconds >= 100);
        Assert.Equal(100, snapshot.TotalCount);
        Assert.Equal(0, snapshot.ErrorCount);
    }
}
