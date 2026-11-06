using System;
using System.Threading;
using System.Threading.Tasks;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;

namespace Backend.API.Services;

/// <summary>
/// 8.211-T3c3a (S11): read-only probe over the financial tables that a monetary base change
/// would reinterpret. Any recorded product, supplier invoice, sale, payment, drawer session,
/// daily closure, receivable or petty-cash expense blocks the transition. Rate history, users,
/// settings and the payment-method catalog are configuration/support data and never count.
/// </summary>
public class MonetaryTransitionGuard : IMonetaryTransitionGuard
{
    private readonly InventoryDbContext _inventoryContext;
    private readonly SalesDbContext _salesContext;

    public MonetaryTransitionGuard(InventoryDbContext inventoryContext, SalesDbContext salesContext)
    {
        ArgumentNullException.ThrowIfNull(inventoryContext);
        ArgumentNullException.ThrowIfNull(salesContext);

        _inventoryContext = inventoryContext;
        _salesContext = salesContext;
    }

    private const long TransitionLockKey = 8211;

    public async Task<IAsyncDisposable?> AcquireTransitionLockAsync(CancellationToken cancellationToken = default)
    {
        if (!_salesContext.Database.IsNpgsql())
        {
            return null;
        }

        await _salesContext.Database.OpenConnectionAsync(cancellationToken);
        try
        {
            await _salesContext.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_lock({0})",
                new object[] { TransitionLockKey },
                cancellationToken);
            return new PostgresTransitionLock(_salesContext, TransitionLockKey);
        }
        catch
        {
            await _salesContext.Database.CloseConnectionAsync();
            throw;
        }
    }

    public async Task<bool> HasFinancialDataAsync(CancellationToken cancellationToken = default)
    {
        // El producto de sistema de adelanto de efectivo se siembra en el arranque y no es
        // catálogo del comercio: no debe impedir la selección inicial de moneda.
        if (await _inventoryContext.Products.AsNoTracking()
            .AnyAsync(product => !product.IsCashAdvance, cancellationToken))
        {
            return true;
        }

        if (await _inventoryContext.SupplierInvoices.AsNoTracking().AnyAsync(cancellationToken))
        {
            return true;
        }

        if (await _salesContext.Sales.AsNoTracking().AnyAsync(cancellationToken))
        {
            return true;
        }

        if (await _salesContext.SalePayments.AsNoTracking().AnyAsync(cancellationToken))
        {
            return true;
        }

        if (await _salesContext.CashDrawerSessions.AsNoTracking().AnyAsync(cancellationToken))
        {
            return true;
        }

        if (await _salesContext.DailyClosures.AsNoTracking().AnyAsync(cancellationToken))
        {
            return true;
        }

        if (await _salesContext.Receivables.AsNoTracking().AnyAsync(cancellationToken))
        {
            return true;
        }

        return await _salesContext.PettyCashExpenses.AsNoTracking().AnyAsync(cancellationToken);
    }

    private sealed class PostgresTransitionLock : IAsyncDisposable
    {
        private readonly SalesDbContext _context;
        private readonly long _key;
        private int _disposed;

        public PostgresTransitionLock(SalesDbContext context, long key)
        {
            _context = context;
            _key = key;
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                await _context.Database.ExecuteSqlRawAsync(
                    "SELECT pg_advisory_unlock({0})",
                    new object[] { _key });
            }
            finally
            {
                await _context.Database.CloseConnectionAsync();
            }
        }
    }
}


