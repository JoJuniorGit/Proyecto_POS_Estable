using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Backend.API.Services;

public class ServiceRestartCoordinator : IServiceRestartCoordinator
{
    private const string ServiceName = "PosBackendService";
    private const int DeferredRestartDelayMs = 2000;

    private readonly ILogger<ServiceRestartCoordinator> _logger;
    private int _isScheduled;

    public ServiceRestartCoordinator(ILogger<ServiceRestartCoordinator> logger)
    {
        _logger = logger;
    }

    public bool TryScheduleRestart()
    {
        if (Interlocked.CompareExchange(ref _isScheduled, 1, 0) != 0)
        {
            return false;
        }

        RunDeferredRestart();
        return true;
    }

    protected virtual void RunDeferredRestart()
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(DeferredRestartDelayMs);
            ExecuteRestart();
        });
    }

    private void ExecuteRestart()
    {
        try
        {
            var nssmPath = Path.Combine(AppContext.BaseDirectory, "nssm.exe");
            if (File.Exists(nssmPath))
            {
                Process.Start(new ProcessStartInfo(nssmPath, $"restart {ServiceName}")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                _logger.LogInformation("Reinicio diferido del servicio {ServiceName} programado via nssm.", ServiceName);
                return;
            }

            Process.Start(new ProcessStartInfo("sc.exe", $"stop {ServiceName}")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Process.Start(new ProcessStartInfo("sc.exe", $"start {ServiceName}")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            _logger.LogInformation("Reinicio diferido del servicio {ServiceName} programado via sc.exe.", ServiceName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "No se pudo ejecutar el reinicio del servicio {ServiceName}.", ServiceName);
        }
    }
}