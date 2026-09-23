using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sales.Module.Data;

namespace Backend.API.Jobs;

/// <summary>
/// Tarea programada en segundo plano para purgar registros expirados de IdempotentRequests
/// en lotes de 1.000 filas con pausas de 100 ms para erradicar bloqueos de tabla (table locks).
/// </summary>
public class IdempotencyCleanupJob : BackgroundService
{
    // 8.7-M4: se inyecta IServiceScopeFactory (no IServiceProvider) para acotar la superficie del contenedor.
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<IdempotencyCleanupJob> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromHours(1);

    public IdempotencyCleanupJob(IServiceScopeFactory scopeFactory, ILogger<IdempotencyCleanupJob> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[IDEMPOTENCY_CLEANUP] Servicio de purga programada iniciado con intervalo de {Interval}.", _interval);

        // Esperar un breve periodo tras el arranque inicial del servidor
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCleanupCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[IDEMPOTENCY_CLEANUP] Error no controlado durante el ciclo de purga de idempotencia: {Message}", ex.Message);
            }

            try
            {
                await Task.Delay(_interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }

        _logger.LogInformation("[IDEMPOTENCY_CLEANUP] Servicio de purga programada finalizado.");
    }

    public async Task<int> RunCleanupCycleAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<SalesDbContext>();

        if (!dbContext.Database.IsRelational())
        {
            return 0;
        }

        // Timeout defensivo de 2 minutos por ciclo de purga
        using var cycleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cycleCts.CancelAfter(TimeSpan.FromMinutes(2));

        int totalPurged = 0;
        DateTime? oldestExpired = null;
        try
        {
            var now = DateTime.UtcNow;
            var expiredQuery = dbContext.IdempotentRequests.AsNoTracking().Where(r => r.ExpiresAtUtc < now);
            int pendingCount = await expiredQuery.CountAsync(cycleCts.Token).ConfigureAwait(false);
            if (pendingCount > 0)
            {
                oldestExpired = await expiredQuery.MinAsync(r => (DateTime?)r.CreatedAtUtc, cycleCts.Token).ConfigureAwait(false);
            }

            while (!cycleCts.Token.IsCancellationRequested)
            {
                // Eliminación en lotes de 1.000 registros para prevenir contención en PostgreSQL
                int deleted = await dbContext.Database.ExecuteSqlRawAsync(
                    "DELETE FROM \"IdempotentRequests\" WHERE \"Id\" IN (SELECT \"Id\" FROM \"IdempotentRequests\" WHERE \"ExpiresAtUtc\" < NOW() LIMIT 1000);",
                    cycleCts.Token).ConfigureAwait(false);

                if (deleted == 0)
                {
                    break;
                }

                totalPurged += deleted;
                await Task.Delay(100, cycleCts.Token).ConfigureAwait(false);
            }

            if (totalPurged > 0)
            {
                _logger.LogInformation("[IDEMPOTENCY_CLEANUP] Purga completada. Total registros eliminados: {TotalPurged}. Registro más antiguo: {OldestExpired:O}.", totalPurged, oldestExpired);
                Core.Logging.AppLogger.LogSecurityAudit($"[IDEMPOTENCY_PURGE] Se purgaron {totalPurged} registros expirados de idempotencia. Fecha de registro más antiguo: {oldestExpired:O}.");
            }
        }
        catch (OperationCanceledException) when (cycleCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("[IDEMPOTENCY_CLEANUP] Ciclo de purga alcanzó el timeout defensivo de 2 minutos. Total purgado en esta iteración: {TotalPurged}.", totalPurged);
        }

        return totalPurged;
    }
}
