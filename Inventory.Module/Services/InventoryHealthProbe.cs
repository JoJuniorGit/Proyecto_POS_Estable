using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Helpers;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Module.Services;

public class InventoryHealthProbe : IInventoryHealthProbe
{
    private readonly InventoryDbContext _context;

    public InventoryHealthProbe(InventoryDbContext context)
    {
        _context = context;
    }

    public Task<decimal?> GetTodayRateAsync(CancellationToken cancellationToken = default)
    {
        var today = TimeZoneHelper.GetVenezuelaDate();
        return _context.ExchangeRateHistory
            .AsNoTracking()
            .Where(e => e.Date == today)
            .Select(e => (decimal?)e.Rate)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public Task<decimal?> GetLatestRateAsync(CancellationToken cancellationToken = default)
        => _context.ExchangeRateHistory
            .AsNoTracking()
            .OrderByDescending(e => e.Date)
            .Select(e => (decimal?)e.Rate)
            .FirstOrDefaultAsync(cancellationToken);
}
