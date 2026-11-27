using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;

namespace Core.Interfaces;

/// <summary>
/// 8.211-T1 (S1/S2): lectura y guardado del perfil monetario configurable. La persistencia reutiliza
/// el almacén de settings existente (sin esquema nuevo) y la escritura es atómica.
/// </summary>
public interface IMonetaryProfileService
{
    Task<MonetaryProfileDto> GetAsync(CancellationToken cancellationToken = default);

    Task<MonetaryProfileDto> SaveAsync(MonetaryProfileDto profile, CancellationToken cancellationToken = default);
}
