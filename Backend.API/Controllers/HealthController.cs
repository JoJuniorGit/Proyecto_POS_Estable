using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Backend.API.DTOs;
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
    public async Task<IActionResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            bool canConnect = await _salesDb.Database.CanConnectAsync(cancellationToken);
            if (canConnect)
            {
                // 8.9-L9: el endpoint de salud es anónimo (lo usan el escaneo LAN y el polling
                // de conectividad); por ello NO se expone la versión exacta del servidor.
                return Ok(new HealthStatusDto
                {
                    Status = "Healthy",
                    Service = "Proyecto_POS_Server",
                    Database = "Connected",
                    Timestamp = DateTime.UtcNow.ToString("o")
                });
            }

            return StatusCode((int)HttpStatusCode.ServiceUnavailable, new HealthStatusDto
            {
                Status = "Unhealthy",
                Service = "Proyecto_POS_Server",
                Database = "Disconnected",
                Message = "La conexión con la base de datos PostgreSQL no está disponible.",
                Timestamp = DateTime.UtcNow.ToString("o")
            });
        }
        catch (Exception ex)
        {
            Core.Logging.AppLogger.LogStart($"[HEALTH_CHECK_ERROR] Error al verificar la salud de la base de datos: {ex.Message}");
            return StatusCode((int)HttpStatusCode.ServiceUnavailable, new HealthStatusDto
            {
                Status = "Unhealthy",
                Service = "Proyecto_POS_Server",
                Database = "Error",
                Message = "El servicio no se encuentra disponible temporalmente.",
                Timestamp = DateTime.UtcNow.ToString("o")
            });
        }
    }

    [NonAction]
    public Task<IActionResult> CheckHealth(CancellationToken cancellationToken = default) => CheckHealthAsync(cancellationToken);

    [HttpGet("api/health/metrics")]
    [Authorize(Roles = "Admin,Manager")]
    public IActionResult GetMetrics()
    {
        var (hits, misses, hitRate) = Core.Metrics.CacheMetrics.GetSnapshot();
        return Ok(new HealthMetricsDto
        {
            CacheHits = hits,
            CacheMisses = misses,
            HitRatePercentage = Math.Round(hitRate, 2),
            Timestamp = DateTime.UtcNow.ToString("o")
        });
    }

    [HttpGet("api/health/requests")]
    [Authorize(Roles = "Admin,Manager")]
    public IActionResult GetRequestMetrics()
    {
        return Ok(new HealthRequestMetricsDto
        {
            Endpoints = _requestMetrics.GetSnapshot(),
            Timestamp = DateTime.UtcNow.ToString("o")
        });
    }

    [HttpGet("api/health/details")]
    [Authorize(Roles = "Admin,Manager")]
    public async Task<IActionResult> GetDetailsAsync(CancellationToken cancellationToken = default)
    {
        var applied = await _salesDb.Database.GetAppliedMigrationsAsync(cancellationToken);
        var pending = _salesDb.Database.GetMigrations().Except(applied);

        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        decimal? bcvToday = await _inventoryDb.ExchangeRateHistory
            .AsNoTracking()
            .Where(e => e.Date == today)
            .Select(e => (decimal?)e.Rate)
            .FirstOrDefaultAsync(cancellationToken);
        decimal? bcvLatest = await _inventoryDb.ExchangeRateHistory
            .AsNoTracking()
            .OrderByDescending(e => e.Date)
            .Select(e => (decimal?)e.Rate)
            .FirstOrDefaultAsync(cancellationToken);

        DateTime? certExpiryUtc = ResolveCertificateExpiry();
        var (lastBackupUtc, lastBackupAgeMinutes, lastBackupFresh) = ResolveLastBackup();

        return Ok(new HealthDetailsDto
        {
            Status = "Healthy",
            MigrationsApplied = applied.Count(),
            PendingMigrations = pending.Count(),
            BcvTodayRate = bcvToday,
            BcvLatestRate = bcvLatest,
            BcvFreshToday = bcvToday.HasValue,
            DiskFreeMb = GetDiskFreeMb(),
            DiskTotalMb = GetDiskTotalMb(),
            CertExpiryUtc = certExpiryUtc,
            LastBackupUtc = lastBackupUtc,
            LastBackupAgeMinutes = lastBackupAgeMinutes,
            LastBackupFresh = lastBackupFresh,
            Timestamp = DateTime.UtcNow.ToString("o")
        });
    }

    [NonAction]
    public Task<IActionResult> GetDetails(CancellationToken cancellationToken = default) => GetDetailsAsync(cancellationToken);

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

    private (DateTime? LastBackupUtc, double? AgeMinutes, bool IsFresh) ResolveLastBackup()
    {
        var result = Backend.API.Metrics.BackupFreshnessEvaluator.Evaluate(
            _configuration["Backup:Directory"],
            _configuration.GetValue<double?>("Backup:MaxAgeHours"));
        return (result.LastBackupUtc, result.AgeMinutes, result.IsFresh);
    }
}
