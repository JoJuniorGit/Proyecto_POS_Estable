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

    /// <summary>
    /// Reclama el secreto efímero de emparejamiento incorporado en el QR (single-use).
    /// Solo es válido desde red local/loopback y mientras no haya sido consumido ni haya expirado.
    /// </summary>
    [HttpPost("claim")]
    [AllowAnonymous]
    public IActionResult ClaimPairingToken([FromBody] ClaimPairingRequest? request)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Token))
        {
            return BadRequest(new { message = "Falta el token de emparejamiento." });
        }

        // Defense in depth: el reclamo solo se admite desde hosts locales o IPs privadas de la LAN.
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        if (remoteIp == null || !IsPrivateOrLocalAddress(remoteIp))
        {
            return StatusCode((int)HttpStatusCode.Forbidden, new
            {
                message = "El emparejamiento solo se admite desde la red local del establecimiento."
            });
        }

        if (!_networkDiscoveryService.TryClaimPairingToken(request.Token))
        {
            return BadRequest(new { message = "Token de emparejamiento inválido, expirado o ya utilizado." });
        }

        return Ok(new { status = "ok", paired = true });
    }

    private static bool IsPrivateOrLocalAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address)) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;

        var bytes = address.GetAddressBytes();
        if (bytes.Length != 4) return false;

        if (bytes[0] == 10) return true;                 // 10.0.0.0/8
        if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true; // 172.16.0.0/12
        if (bytes[0] == 192 && bytes[1] == 168) return true;                  // 192.168.0.0/16
        if (bytes[0] == 169 && bytes[1] == 254) return true;                  // 169.254.0.0/16
        return false;
    }
}

public class ClaimPairingRequest
{
    public string? Token { get; set; }
}
