using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;

namespace Sales.Module.Services;

public class SalesHealthProbe : ISalesHealthProbe
{
    private readonly SalesDbContext _context;

    public SalesHealthProbe(SalesDbContext context)
    {
        _context = context;
    }

    public Task<bool> CanConnectAsync(CancellationToken cancellationToken = default)
        => _context.Database.CanConnectAsync(cancellationToken);

    public async Task<(int AppliedCount, int PendingCount)> GetMigrationStatusAsync(CancellationToken cancellationToken = default)
    {
        var applied = await _context.Database.GetAppliedMigrationsAsync(cancellationToken);
        var pending = _context.Database.GetMigrations().Except(applied);
        return (applied.Count(), pending.Count());
    }
}
