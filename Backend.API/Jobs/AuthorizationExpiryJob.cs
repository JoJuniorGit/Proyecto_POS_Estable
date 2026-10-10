using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Backend.API.Jobs;

/// <summary>
/// 8.150-T6 (spec S5 / design D6): barrido periodico de solicitudes de autorizacion vencidas.
/// Cada tick delega en <see cref="IAuthorizationCoordinator"/> (expira en lote, audita y hace
/// push de AuthorizationExpired). Es un notificador, nunca el unico enforcement: la expiracion
/// perezosa de los caminos resolve/consume sigue vigente, y un tick fallido solo se registra.
/// </summary>
public class AuthorizationExpiryJob : BackgroundService
{
    private const int DefaultSweepSeconds = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuthorizationExpiryJob> _logger;
    private readonly TimeSpan _sweepInterval;

    public AuthorizationExpiryJob(
        IServiceScopeFactory scopeFactory,
        ILogger<AuthorizationExpiryJob> logger,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(configuration);

        _scopeFactory = scopeFactory;
        _logger = logger;

        // 8.150-T6: un valor no positivo se descarta y se conserva el default para no
        // transformar la configuracion en un bucle sin espera.
        var configuredSeconds = configuration.GetValue<int?>("Authorization:ExpirySweepSeconds") ?? DefaultSweepSeconds;
        _sweepInterval = TimeSpan.FromSeconds(configuredSeconds > 0 ? configuredSeconds : DefaultSweepSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[AUTHORIZATION_EXPIRY] Servicio de barrido de autorizaciones vencidas iniciado con intervalo de {Interval}.", _sweepInterval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var coordinator = scope.ServiceProvider.GetRequiredService<IAuthorizationCoordinator>();
                var expiredCount = await coordinator.ExpireStaleAsync(stoppingToken).ConfigureAwait(false);

                if (expiredCount > 0)
                {
                    _logger.LogInformation("[AUTHORIZATION_EXPIRY] Se expiraron {Count} solicitudes vencidas.", expiredCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AUTHORIZATION_EXPIRY] Error no controlado durante el barrido de autorizaciones vencidas: {Message}", ex.Message);
            }

            try
            {
                await Task.Delay(_sweepInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("[AUTHORIZATION_EXPIRY] Servicio de barrido de autorizaciones vencidas finalizado.");
    }
}
