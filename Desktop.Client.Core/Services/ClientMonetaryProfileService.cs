using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Core.DTOs;

namespace Desktop.Client.Services;

/// <summary>
/// 8.211-T4c1 (S9): cliente HTTP read-only del perfil monetario. La lectura es best-effort: un
/// backend caído, una sesión sin token o un payload inválido no deben bloquear el arranque de la
/// app ni romper el display; en todos esos casos se devuelve <c>null</c> y el llamador aplica los
/// defaults Dual USD/VES.
/// </summary>
public class ClientMonetaryProfileService : IClientMonetaryProfileService
{
    private const string Route = "api/client/monetary-profile";

    private readonly HttpClient _httpClient;

    public ClientMonetaryProfileService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<MonetaryProfileDto?> GetAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _httpClient.GetAsync(Route, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<MonetaryProfileDto>(cancellationToken);
        }
        catch (Exception)
        {
            // Incluye 401/403, errores de red, timeouts/cancelación y JSON malformado.
            return null;
        }
    }
}
