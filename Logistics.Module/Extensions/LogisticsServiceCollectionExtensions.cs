using Microsoft.Extensions.DependencyInjection;
using Logistics.Module.Services;

namespace Logistics.Module.Extensions;

public static class LogisticsServiceCollectionExtensions
{
    public static IServiceCollection AddLogisticsModule(this IServiceCollection services)
    {
        services.AddSingleton<IDeliveryService, DeliveryService>();
        return services;
    }
}
