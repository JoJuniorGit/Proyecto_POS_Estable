using Core.DTOs;
using Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

/// <summary>
/// 8.211-T4b1 (S9): perfil monetario de solo lectura para clientes. Cualquier usuario
/// autenticado (incluido Cashier) puede consultar el modo, los códigos y los símbolos vigentes
/// para presentación. No muta configuración ni emite SignalR; el PUT sigue perteneciendo al
/// endpoint administrativo de settings.
/// </summary>
[Authorize]
[ApiController]
[Route("api/client")]
public class MonetaryProfileClientController : ControllerBase
{
    private readonly IMonetaryProfileService _monetaryProfileService;

    public MonetaryProfileClientController(IMonetaryProfileService? monetaryProfileService = null)
    {
        _monetaryProfileService = monetaryProfileService
            ?? throw new InvalidOperationException("El servicio de perfil monetario no está configurado.");
    }

    [HttpGet("monetary-profile")]
    public async Task<ActionResult<MonetaryProfileDto>> GetMonetaryProfileAsync(
        CancellationToken cancellationToken = default)
    {
        return Ok(await _monetaryProfileService.GetAsync(cancellationToken));
    }
}
