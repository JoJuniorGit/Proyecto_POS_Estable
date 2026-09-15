using System;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Entities;
using Sales.Module.Exceptions;

namespace Sales.Module.Services;

public partial class SalesService
{
    public async Task<SaleDto> ClaimSaleAsync(int saleId, SaleClaimAction action, int? actingUserId, CancellationToken cancellationToken = default)
    {
        if (action == SaleClaimAction.None)
        {
            throw new ArgumentException("La acción de reclamo debe ser Editing o Checkout.");
        }

        if (actingUserId == null)
        {
            throw new ArgumentException("Se requiere un usuario autenticado para retomar un pedido en espera.");
        }

        var sale = await GetSaleEntityAsync(saleId);
        EnsureOnHoldStatus(sale);

        if (sale.ClaimedByUserId != null && sale.ClaimedByUserId != actingUserId)
        {
            throw BuildSaleLockedException(sale);
        }

        var userName = await ResolveClaimUserNameAsync(actingUserId.Value, cancellationToken);
        ApplyHoldClaim(sale, actingUserId.Value, userName, action);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await _context.Entry(sale).ReloadAsync(cancellationToken);
            EnsureOnHoldStatus(sale);

            if (sale.ClaimedByUserId != null && sale.ClaimedByUserId != actingUserId)
            {
                throw BuildSaleLockedException(sale);
            }

            ApplyHoldClaim(sale, actingUserId.Value, userName, action);
            await _context.SaveChangesAsync(cancellationToken);
        }

        await NotifyHoldOrdersChangedAsync();
        return await GetSaleAsync(saleId);
    }

    public async Task<SaleDto> ReleaseSaleAsync(int saleId, int? actingUserId, bool force = false, CancellationToken cancellationToken = default)
    {
        var sale = await GetSaleEntityAsync(saleId);

        if (sale.ClaimedByUserId != null && !force && sale.ClaimedByUserId != actingUserId)
        {
            throw BuildSaleLockedException(sale);
        }

        if (sale.Status != SaleStatus.OnHold)
        {
            if (HasHoldClaim(sale))
            {
                ClearHoldClaim(sale);
                await _context.SaveChangesAsync(cancellationToken);
            }

            return await GetSaleAsync(saleId);
        }

        ClearHoldClaim(sale);

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await _context.Entry(sale).ReloadAsync(cancellationToken);

            if (sale.ClaimedByUserId == null)
            {
                return await GetSaleAsync(saleId);
            }

            if (!force && sale.ClaimedByUserId != actingUserId)
            {
                throw BuildSaleLockedException(sale);
            }

            ClearHoldClaim(sale);
            await _context.SaveChangesAsync(cancellationToken);
        }

        await NotifyHoldOrdersChangedAsync();
        return await GetSaleAsync(saleId);
    }

    private static void EnsureHoldClaimAccess(Sale sale, int? actingUserId)
    {
        if (sale.Status != SaleStatus.OnHold) return;
        if (sale.ClaimedByUserId != null && sale.ClaimedByUserId == actingUserId) return;
        if (sale.ClaimedByUserId != null) throw BuildSaleLockedException(sale);
        throw new HoldNotClaimedException(sale.Id);
    }

    private static void ClearHoldClaim(Sale sale)
    {
        sale.ClaimedByUserId = null;
        sale.ClaimedByUserName = null;
        sale.ClaimAction = SaleClaimAction.None;
        sale.ClaimedAtUtc = null;
    }

    private static bool HasHoldClaim(Sale sale) =>
        sale.ClaimedByUserId != null
        || !string.IsNullOrWhiteSpace(sale.ClaimedByUserName)
        || sale.ClaimAction != SaleClaimAction.None
        || sale.ClaimedAtUtc != null;

    private static void EnsureOnHoldStatus(Sale sale)
    {
        if (sale.Status != SaleStatus.OnHold)
        {
            throw new InvalidOperationException("Solo se pueden retomar pedidos en estado En Espera.");
        }
    }

    private static void ApplyHoldClaim(Sale sale, int actingUserId, string userName, SaleClaimAction action)
    {
        sale.ClaimedByUserId = actingUserId;
        sale.ClaimedByUserName = userName;
        sale.ClaimAction = action;
        sale.ClaimedAtUtc = DateTime.UtcNow;
    }

    private static SaleLockedException BuildSaleLockedException(Sale sale) =>
        new(sale.Id, sale.ClaimedByUserId, sale.ClaimedByUserName, sale.ClaimAction.ToString(), sale.ClaimedAtUtc);

    private async Task<string> ResolveClaimUserNameAsync(int actingUserId, CancellationToken cancellationToken)
    {
        var user = await _context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == actingUserId, cancellationToken);

        if (user == null) return "Usuario Desconocido";
        if (!string.IsNullOrWhiteSpace(user.Name)) return user.Name;
        if (!string.IsNullOrWhiteSpace(user.FullName)) return user.FullName;
        return "Usuario Desconocido";
    }
}
