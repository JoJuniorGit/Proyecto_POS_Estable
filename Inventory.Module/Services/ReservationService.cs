using System;
using System.Threading;
using System.Threading.Tasks;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;

namespace Inventory.Module.Services;

public class ReservationService : IReservationService
{
    private readonly InventoryDbContext _context;

    public ReservationService(InventoryDbContext context)
    {
        _context = context;
    }

    public async Task<int> GetActiveReservationsCountAsync(string referenceId, CancellationToken cancellationToken = default)
    {
        return await _context.StockReservations
            .AsNoTracking()
            .CountAsync(r => !r.IsConfirmed && r.ExpiryDate > DateTime.UtcNow && r.ReferenceId == referenceId, cancellationToken);
    }

    public async Task<bool?> ValidateReservationOwnershipAsync(int reservationId, string userRef, CancellationToken cancellationToken = default)
    {
        var reservation = await _context.StockReservations
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == reservationId, cancellationToken);

        if (reservation == null) return null;
        return reservation.ReferenceId != null && reservation.ReferenceId == userRef;
    }
}
