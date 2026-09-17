using Core.Interfaces;
using Moq;
using Sales.Module.Interfaces;
using Sales.Module.Services;

namespace CommandCenter.Tests.TestHelpers;

public static class DailyClosureTestHelper
{
    public static (Mock<ITodayExchangeRateProvider> rateProvider, Mock<ICashDrawerService> cashDrawerService) CreateMocks()
    {
        var rateProvider = new Mock<ITodayExchangeRateProvider>();
        rateProvider.Setup(r => r.GetEffectiveTodayRateAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(50m);

        var cashDrawerService = new Mock<ICashDrawerService>();
        return (rateProvider, cashDrawerService);
    }

    public static DailyClosureService CreateService(Sales.Module.Data.SalesDbContext context)
    {
        var (rateProvider, cashDrawerService) = CreateMocks();
        return new DailyClosureService(context, rateProvider.Object, cashDrawerService.Object);
    }

    public static DailyClosureService CreateService(
        Sales.Module.Data.SalesDbContext context,
        Mock<ITodayExchangeRateProvider> rateProvider,
        Mock<ICashDrawerService> cashDrawerService)
    {
        return new DailyClosureService(context, rateProvider.Object, cashDrawerService.Object);
    }
}
