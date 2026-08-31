using System;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;

namespace Backend.API.Controllers;

[ApiController]
[AllowAnonymous]
public class HealthController : ControllerBase
{
    private readonly SalesDbContext _salesDb;

    public HealthController(SalesDbContext salesDb)
    {
        _salesDb = salesDb;
    }

    [HttpGet("health")]
    [HttpGet("api/health")]
    public async Task<IActionResult> CheckHealth()
    {
        try
        {
            bool canConnect = await _salesDb.Database.CanConnectAsync();
            if (canConnect)
            {
                return Ok(new
                {
                    status = "Healthy",
                    service = "Proyecto_POS_Server",
                    machineName = Environment.MachineName,
                    version = "1.0.0",
                    database = "Connected",
                    timestamp = DateTime.UtcNow.ToString("o")
                });
            }

            return StatusCode((int)HttpStatusCode.ServiceUnavailable, new
            {
                status = "Unhealthy",
                service = "Proyecto_POS_Server",
                machineName = Environment.MachineName,
                version = "1.0.0",
                database = "Disconnected",
                message = "La conexión con la base de datos PostgreSQL no está disponible.",
                timestamp = DateTime.UtcNow.ToString("o")
            });
        }
        catch (Exception ex)
        {
            return StatusCode((int)HttpStatusCode.ServiceUnavailable, new
            {
                status = "Unhealthy",
                service = "Proyecto_POS_Server",
                machineName = Environment.MachineName,
                version = "1.0.0",
                database = "Error",
                message = ex.Message,
                timestamp = DateTime.UtcNow.ToString("o")
            });
        }
    }
}
