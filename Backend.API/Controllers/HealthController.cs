using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Backend.API.DTOs;
using Core.Interfaces;

namespace Backend.API.Controllers;

[ApiController]
public class HealthController : ControllerBase
{
    private readonly ISalesHealthProbe _salesHealthProbe;
    private readonly IInventoryHealthProbe _inventoryHealthProbe;
    private readonly IConfiguration _configuration;
    private readonly IWebHostEnvironment _environment;
    private readonly Backend.API.Metrics.RequestMetricsRegistry _requestMetrics;

    public HealthController(
        ISalesHealthProbe salesHealthProbe,
        IInventoryHealthProbe inventoryHealthProbe,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        Backend.API.Metrics.RequestMetricsRegistry requestMetrics)
    {
        _salesHealthProbe = salesHealthProbe;
        _inventoryHealthProbe = inventoryHealthProbe;
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
            bool canConnect = await _salesHealthProbe.CanConnectAsync(cancellationToken);
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
        var (migrationsApplied, pendingMigrations) = await _salesHealthProbe.GetMigrationStatusAsync(cancellationToken);
        decimal? bcvToday = await _inventoryHealthProbe.GetTodayRateAsync(cancellationToken);
        decimal? bcvLatest = await _inventoryHealthProbe.GetLatestRateAsync(cancellationToken);

        DateTime? certExpiryUtc = ResolveCertificateExpiry();
        var (lastBackupUtc, lastBackupAgeMinutes, lastBackupFresh) = ResolveLastBackup();
        var (convStatus, convVersion, convError) = Backend.API.Startup.StartupDiagnostics.GetConvergence();

        return Ok(new HealthDetailsDto
        {
            Status = "Healthy",
            MigrationsApplied = migrationsApplied,
            PendingMigrations = pendingMigrations,
            BcvTodayRate = bcvToday,
            BcvLatestRate = bcvLatest,
            BcvFreshToday = bcvToday.HasValue,
            DiskFreeMb = GetDiskFreeMb(),
            DiskTotalMb = GetDiskTotalMb(),
            CertExpiryUtc = certExpiryUtc,
            LastBackupUtc = lastBackupUtc,
            LastBackupAgeMinutes = lastBackupAgeMinutes,
            LastBackupFresh = lastBackupFresh,
            ConvergenceStatus = convStatus,
            ConvergenceVersion = convVersion,
            ConvergenceError = convError,
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
