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
        // Nota: UseForwardedHeaders garantiza que si la petición proviene de un reverse proxy,
        // RemoteIpAddress reflejará la IP del cliente real.
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        bool isLocal = remoteIp != null && (
                       IPAddress.IsLoopback(remoteIp) 
                       || (remoteIp.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remoteIp.MapToIPv4()))
                       || remoteIp.ToString() == "127.0.0.1" 
                       || remoteIp.ToString() == "::1");

        // 2. Si no es local, verificar que el usuario esté autenticado con rol elevado (Admin/Manager).
        // 8.7-L1: un cajero no debe poder leer IPs/puertos/QR del establecimiento.
        if (!isLocal && !(User.Identity?.IsAuthenticated ?? false))
        {
            return StatusCode((int)HttpStatusCode.Forbidden, new
            {
                message = "El acceso a la información de emparejamiento está restringido a la máquina local o usuarios autenticados."
            });
        }

        if (!isLocal && !User.IsInRole("Admin") && !User.IsInRole("Manager"))
        {
            return StatusCode((int)HttpStatusCode.Forbidden, new
            {
                message = "El acceso a la información de emparejamiento requiere rol de Administrador o Supervisor."
            });
        }

        bool isHttps = Request.IsHttps 
            || System.IO.File.Exists(System.IO.Path.Combine(AppContext.BaseDirectory, "pos-https.pfx"))
            || System.IO.File.Exists("pos-https.pfx");

        var info = _networkDiscoveryService.GetPairingInfo(httpPort: 5000, httpsPort: 5001, isHttpsEnabled: isHttps);
        return Ok(info);
    }
}
