using Core.Interfaces;
using Inventory.Module.Data;
using Sales.Module.Interfaces;

namespace Backend.API.Services;

public class TodayExchangeRateProvider : ITodayExchangeRateProvider
{
    private readonly InventoryDbContext _inventoryContext;
    private readonly ICashDrawerService _cashDrawerService;

    public TodayExchangeRateProvider(InventoryDbContext inventoryContext, ICashDrawerService cashDrawerService)
    {
        _inventoryContext = inventoryContext;
        _cashDrawerService = cashDrawerService;
    }

    public Task<decimal> GetEffectiveTodayRateAsync(CancellationToken cancellationToken)
    {
        return ExchangeRateResolver.ReadEffectiveTodayRateAsync(_inventoryContext, _cashDrawerService);
    }
}
