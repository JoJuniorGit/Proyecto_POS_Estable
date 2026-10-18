using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sales.Module.Interfaces;

namespace Backend.API.Jobs;

/// <summary>
/// 8.187-T6 (H1, S3/S4): barrido periódico de cartera CxC. Materializa el estado Vencida de las
/// obligaciones abiertas fuera del período de gracia y aplica la suspensión/reactivación
/// automática por morosidad del perfil crediticio. Es idempotente (una segunda corrida no cambia
/// nada) y su intervalo por defecto de 6 h garantiza que la materialización diaria ocurra aunque
/// un ciclo se pierda o el host arranque a mitad de jornada; un tick fallido solo se registra.
/// </summary>
public class CreditMoraJob : BackgroundService
{
    private const int DefaultIntervalHours = 6;

    // 8.7-M4: se inyecta IServiceScopeFactory (no IServiceProvider) para acotar la superficie del contenedor.
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<CreditMoraJob> _logger;
    private readonly TimeSpan _interval;

    public CreditMoraJob(
        IServiceScopeFactory scopeFactory,
        ILogger<CreditMoraJob> logger,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(configuration);

        _scopeFactory = scopeFactory;
        _logger = logger;

        // Un valor no positivo se descarta y se conserva el default: la mora es un estado
        // financiero del sistema y el barrido no se desactiva por configuración.
        var configuredHours = configuration.GetValue<int?>("CreditMora:IntervalHours") ?? DefaultIntervalHours;
        _interval = TimeSpan.FromHours(configuredHours > 0 ? configuredHours : DefaultIntervalHours);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[CREDIT_MORA] Barrido programado de cartera CxC iniciado con intervalo de {Interval}.", _interval);

        using var timer = new PeriodicTimer(_interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var creditService = scope.ServiceProvider.GetRequiredService<ICreditService>();
                var result = await creditService.EvaluateOverdueAndMoraAsync(stoppingToken).ConfigureAwait(false);

                _logger.LogInformation(
                    "[CREDIT_MORA] Barrido completado: {Materialized} obligaciones materializadas como Vencida, {Suspended} clientes suspendidos por mora, {Reactivated} clientes reactivados, {Evaluated} clientes con crédito evaluados.",
                    result.OverdueMaterializedCount,
                    result.SuspendedCount,
                    result.ReactivatedCount,
                    result.EvaluatedCustomersCount);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CREDIT_MORA] Error no controlado durante el barrido de cartera CxC: {Message}", ex.Message);
            }

            try
            {
                // La primera espera ocurre recién después de la corrida inicial del arranque.
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("[CREDIT_MORA] Barrido programado de cartera CxC finalizado.");
    }
}
