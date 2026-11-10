using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;

namespace Desktop.Client.Services;

/// <summary>
/// 8.211-T4c1 (S9): lectura del perfil monetario activo para la presentación del cliente WPF.
/// El endpoint es read-only y está disponible para cualquier rol autenticado; la mutación del
/// perfil sigue perteneciendo al endpoint administrativo de settings.
/// </summary>
public interface IClientMonetaryProfileService
{
    /// <summary>
    /// Devuelve el perfil de presentación o <c>null</c> cuando la lectura falla (red, timeout,
    /// 401/403, 5xx, JSON inválido o cancelación). Nunca lanza: el cliente conserva sus defaults.
    /// </summary>
    Task<MonetaryProfileDto?> GetAsync(CancellationToken cancellationToken = default);
}
