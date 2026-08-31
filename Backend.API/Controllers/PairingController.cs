using System.Net;
using Backend.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PairingController : ControllerBase
{
    private readonly INetworkDiscoveryService _networkDiscoveryService;

    public PairingController(INetworkDiscoveryService networkDiscoveryService)
    {
        _networkDiscoveryService = networkDiscoveryService;
    }

    /// <summary>
    /// Devuelve información de emparejamiento de red local (IPs físicas, puertos, URLs y QR payload).
    /// Por seguridad, este endpoint solo es accesible desde peticiones locales (localhost) o usuarios autenticados.
    /// </summary>
    [HttpGet("info")]
    [AllowAnonymous]
    public IActionResult GetPairingInfo()
    {
        // 1. Verificar si la petición es local (Loopback / Localhost)
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        bool isLocal = remoteIp == null 
                       || IPAddress.IsLoopback(remoteIp) 
                       || remoteIp.ToString() == "127.0.0.1" 
                       || remoteIp.ToString() == "::1";

        // 2. Si no es local, verificar si el usuario está autenticado
        if (!isLocal && !(User.Identity?.IsAuthenticated ?? false))
        {
            return StatusCode((int)HttpStatusCode.Forbidden, new
            {
                message = "El acceso a la información de emparejamiento está restringido a la máquina local o usuarios autenticados."
            });
        }

        var info = _networkDiscoveryService.GetPairingInfo(httpPort: 5000, httpsPort: 5001);
        return Ok(info);
    }
}
