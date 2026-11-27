using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;

namespace Core.Interfaces;

/// <summary>
/// 8.211-T3c3b3 (S7/S11): prepares the payment-method catalog for a freshly configured Single
/// profile. The operation is idempotent and only ever touches unused catalog rows; methods already
/// referenced by financial records keep their currency and flags.
/// </summary>
public interface IMonetaryPaymentMethodSeeder
{
    Task EnsureForProfileAsync(MonetaryProfileDto profile, CancellationToken cancellationToken = default);
}
