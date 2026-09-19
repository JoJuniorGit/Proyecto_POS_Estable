using System.Threading;
using System.Threading.Tasks;

namespace Core.Interfaces;

public interface IReservationService
{
    Task<int> GetActiveReservationsCountAsync(string referenceId, CancellationToken cancellationToken = default);
    Task<bool?> ValidateReservationOwnershipAsync(int reservationId, string userRef, CancellationToken cancellationToken = default);
}
