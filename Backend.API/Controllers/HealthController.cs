using System;
using System.IO;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Inventory.Module.Data;
using Sales.Module.Data;

namespace Backend.API.Controllers;

[ApiController]
public class HealthController : ControllerBase
{
    private readonly SalesDbContext _salesDb;
    private readonly InventoryDbContext _inventoryDb;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly Backend.API.Metrics.RequestMetricsRegistry _requestMetrics;

    public HealthController(
        SalesDbContext salesDb,
        InventoryDbContext inventoryDb,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        Backend.API.Metrics.RequestMetricsRegistry requestMetrics)
    {
        _salesDb = salesDb;
        _inventoryDb = inventoryDb;
        _configuration = configuration;
        _environment = environment;
        _requestMetrics = requestMetrics;
    }

    [AllowAnonymous]
    [HttpGet("health")]
    [HttpGet("api/health")]
    public async Task<IActionResult> CheckHealth()
    {
        try
        {
            bool canConnect = await _salesDb.Database.CanConnectAsync();
            if (canConnect)
            {
                // 8.9-L9: el endpoint de salud es anónimo (lo usan el escaneo LAN y el polling
                // de conectividad); por ello NO se expone la versión exacta del servidor.
                return Ok(new
                {
                    status = "Healthy",
                    service = "Proyecto_POS_Server",
                    database = "Connected",
                    timestamp = DateTime.UtcNow.ToString("o")
                });
            }

            return StatusCode((int)HttpStatusCode.ServiceUnavailable, new
            {
                status = "Unhealthy",
                service = "Proyecto_POS_Server",
                database = "Disconnected",
                message = "La conexión con la base de datos PostgreSQL no está disponible.",
                timestamp = DateTime.UtcNow.ToString("o")
            });
        }
        catch (Exception ex)
        {
            Core.Logging.AppLogger.LogStart($"[HEALTH_CHECK_ERROR] Error al verificar la salud de la base de datos: {ex.Message}");
            return StatusCode((int)HttpStatusCode.ServiceUnavailable, new
            {
                status = "Unhealthy",
                service = "Proyecto_POS_Server",
                database = "Error",
                message = "El servicio no se encuentra disponible temporalmente.",
                timestamp = DateTime.UtcNow.ToString("o")
            });
        }
    }

    [HttpGet("api/health/metrics")]
    [Authorize(Roles = "Admin,Manager")]
    public IActionResult GetMetrics()
    {
        var (hits, misses, hitRate) = Core.Metrics.CacheMetrics.GetSnapshot();
        return Ok(new
        {
            cacheHits = hits,
            cacheMisses = misses,
            hitRatePercentage = Math.Round(hitRate, 2),
            timestamp = DateTime.UtcNow.ToString("o")
        });
    }

    [HttpGet("api/health/requests")]
    [Authorize(Roles = "Admin,Manager")]
    public IActionResult GetRequestMetrics()
    {
        return Ok(new
        {
            endpoints = _requestMetrics.GetSnapshot(),
            timestamp = DateTime.UtcNow.ToString("o")
        });
    }

    [HttpGet("api/health/details")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> GetDetails()
    {
        var applied = await _salesDb.Database.GetAppliedMigrationsAsync();
        var pending = await _salesDb.Database.GetPendingMigrationsAsync();

        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        decimal? bcvToday = await _inventoryDb.ExchangeRateHistory
            .AsNoTracking()
            .Where(e => e.Date == today)
            .Select(e => (decimal?)e.Rate)
            .FirstOrDefaultAsync();
        decimal? bcvLatest = await _inventoryDb.ExchangeRateHistory
            .AsNoTracking()
            .OrderByDescending(e => e.Date)
            .Select(e => (decimal?)e.Rate)
            .FirstOrDefaultAsync();

        DateTime? certExpiryUtc = ResolveCertificateExpiry();

        return Ok(new
        {
            status = "Healthy",
            migrationsApplied = applied.Count(),
            pendingMigrations = pending.Count(),
            bcvTodayRate = bcvToday,
            bcvLatestRate = bcvLatest,
            bcvFreshToday = bcvToday.HasValue,
            diskFreeMb = GetDiskFreeMb(),
            diskTotalMb = GetDiskTotalMb(),
            certExpiryUtc = certExpiryUtc,
            timestamp = DateTime.UtcNow.ToString("o")
        });
    }

    private DateTime? ResolveCertificateExpiry()
    {
        try
        {
            var path = _configuration["Kestrel:Certificates:Default:Path"];
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var certPath = Path.IsPathRooted(path) ? path : Path.Combine(_environment.ContentRootPath, path);
            if (!System.IO.File.Exists(certPath))
            {
                return null;
            }

            var password = _configuration["Kestrel:Certificates:Default:Password"];
            using var cert = string.IsNullOrEmpty(password)
                ? System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(certPath, null)
                : System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadPkcs12FromFile(certPath, password);
            return cert.NotAfter.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private double GetDiskFreeMb()
    {
        try
        {
            var drive = new DriveInfo(_environment.ContentRootPath);
            return Math.Round(drive.AvailableFreeSpace / 1024d / 1024d, 0);
        }
        catch
        {
            return 0;
        }
    }

    private double GetDiskTotalMb()
    {
        try
        {
            var drive = new DriveInfo(_environment.ContentRootPath);
            return Math.Round(drive.TotalSize / 1024d / 1024d, 0);
        }
        catch
        {
            return 0;
        }
    }
}
