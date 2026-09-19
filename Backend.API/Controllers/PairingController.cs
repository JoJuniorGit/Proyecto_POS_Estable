using System.Net;
using Backend.API.DTOs;
using Backend.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Backend.API.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PairingController : ControllerBase
{
    private readonly INetworkDiscoveryService _networkDiscoveryService;
    private readonly Microsoft.Extensions.Configuration.IConfiguration _configuration;
    private readonly HttpsRuntimeInfo _httpsRuntimeInfo;

    public PairingController(INetworkDiscoveryService networkDiscoveryService, Microsoft.Extensions.Configuration.IConfiguration configuration, HttpsRuntimeInfo httpsRuntimeInfo)
    {
        _networkDiscoveryService = networkDiscoveryService;
        _configuration = configuration;
        _httpsRuntimeInfo = httpsRuntimeInfo;
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
        // Preferir HttpContext.Connection.LocalIpAddress si se usa X-Forwarded-For para evitar spoofing.
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        
        bool isLocal = remoteIp != null && (
                       IPAddress.IsLoopback(remoteIp) 
                       || (remoteIp.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(remoteIp.MapToIPv4()))
                       || remoteIp.ToString() == "127.0.0.1" 
                       || remoteIp.ToString() == "::1");

        // Si hay cabeceras de proxy, usamos LocalIpAddress o Request.Host como validación adicional si es necesario, 
        // pero principalmente revocamos isLocal si detectamos spoofing.
        if (isLocal && (Request.Headers.ContainsKey("X-Forwarded-For") || Request.Headers.ContainsKey("X-Forwarded-Host")))
        {
            // Validar de forma más estricta con LocalIpAddress
            var localIp = HttpContext.Connection.LocalIpAddress;
            if (localIp == null || (!IPAddress.IsLoopback(localIp) && localIp.ToString() != "127.0.0.1" && localIp.ToString() != "::1"))
            {
                isLocal = false;
            }
        }

        // 2. Si no es local, verificar que el usuario esté autenticado con rol elevado (Admin/Manager).
        if (!isLocal && !(User.Identity?.IsAuthenticated ?? false))
        {
            return this.ApiForbidden("El acceso a la información de emparejamiento está restringido a la máquina local o usuarios autenticados.");
        }

        if (!isLocal && !User.IsInRole("Admin") && !User.IsInRole("Manager"))
        {
            return this.ApiForbidden("El acceso a la información de emparejamiento requiere rol de Administrador o Supervisor.");
        }

        bool isHttps = _httpsRuntimeInfo.Enabled || Request.IsHttps;

        int httpPort = _httpsRuntimeInfo.HttpPort;
        int httpsPort = _httpsRuntimeInfo.HttpsPort;
        try 
        {
            if (_configuration != null) 
            {
                if (int.TryParse(_configuration["Ports:Http"], out int configHttp)) httpPort = configHttp;
                if (int.TryParse(_configuration["Ports:Https"], out int configHttps)) httpsPort = configHttps;
            }
        } 
        catch { }

        var info = _networkDiscoveryService.GetPairingInfo(httpPort: httpPort, httpsPort: httpsPort, isHttpsEnabled: isHttps);
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
            return this.ApiBadRequest("Falta el token de emparejamiento.");
        }

        // Defense in depth: el reclamo solo se admite desde hosts locales o IPs privadas de la LAN.
        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        if (remoteIp == null || !IsPrivateOrLocalAddress(remoteIp))
        {
            return this.ApiForbidden("El emparejamiento solo se admite desde la red local del establecimiento.");
        }

        if (!_networkDiscoveryService.TryClaimPairingToken(request.Token))
        {
            return this.ApiBadRequest("Token de emparejamiento inválido, expirado o ya utilizado.");
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
