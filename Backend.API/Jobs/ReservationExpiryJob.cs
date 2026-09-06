using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Core.Interfaces;
using Inventory.Module.Data;

namespace Backend.API.Jobs;

/// <summary>
/// Tarea programada en segundo plano para expirar automáticamente reservas de inventario
/// que no hayan sido confirmadas antes de su ExpiryDate [8I-CR2].
/// Libera atómicamente el stock reservado devolviéndolo al disponible.
/// </summary>
public class ReservationExpiryJob : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<ReservationExpiryJob> _logger;
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(60);

    public ReservationExpiryJob(IServiceProvider serviceProvider, ILogger<ReservationExpiryJob> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[RESERVATION_EXPIRY] Servicio de expiración automática de reservas iniciado con intervalo de {Interval}.", _interval);

        // Breve pausa inicial tras el arranque del backend
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunExpiryCycleAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RESERVATION_EXPIRY] Error no controlado durante el ciclo de expiración de reservas: {Message}", ex.Message);
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

        _logger.LogInformation("[RESERVATION_EXPIRY] Servicio de expiración automática de reservas finalizado.");
    }

    /// <summary>
    /// Ejecuta un ciclo de expiración y cancelación de reservas vencidas.
    /// Retorna la cantidad de reservas vencidas liberadas.
    /// </summary>
    public async Task<int> RunExpiryCycleAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<InventoryDbContext>();
        var inventoryService = scope.ServiceProvider.GetRequiredService<IInventoryService>();

        var now = DateTime.UtcNow;

        var expiredIds = await dbContext.StockReservations
            .AsNoTracking()
            .Where(r => !r.IsConfirmed && r.ExpiryDate <= now)
            .OrderBy(r => r.ExpiryDate)
            .Select(r => r.Id)
            .Take(100)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (expiredIds.Count == 0)
        {
            return 0;
        }

        int releasedCount = 0;
        foreach (var reservationId in expiredIds)
        {
            try
            {
                await inventoryService.CancelReservationAsync(reservationId).ConfigureAwait(false);
                releasedCount++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[RESERVATION_EXPIRY] Fallo al liberar la reserva expirada #{ReservationId}: {Message}", reservationId, ex.Message);
            }
        }

        if (releasedCount > 0)
        {
            _logger.LogInformation("[RESERVATION_EXPIRY] Ciclo completado. Se liberaron {Count} reservas vencidas.", releasedCount);
            Core.Logging.AppLogger.LogSecurityAudit($"[RESERVATION_EXPIRY] Se liberaron {releasedCount} reservas vencidas de stock no confirmadas.");
        }

        return releasedCount;
    }
}
