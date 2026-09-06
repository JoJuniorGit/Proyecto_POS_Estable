using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Hubs;
using Core.Entities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sales.Module.Data;

namespace Backend.API.Jobs;

public class OutboxProcessorJob : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<OutboxProcessorJob> _logger;

    public OutboxProcessorJob(IServiceProvider serviceProvider, ILogger<OutboxProcessorJob> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    private DateTime _lastPurgeCheckUtc = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[OutboxProcessor] Background Service iniciado con ciclo de polling de 2 segundos.");
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));

        try
        {
            // Initial wait for database and schema migration to settle
            await Task.Delay(3000, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await ProcessPendingMessagesAsync(stoppingToken);

                // Ejecutar purga de mensajes procesados con más de 7 días de antigüedad (cada 1 hora)
                if (DateTime.UtcNow - _lastPurgeCheckUtc >= TimeSpan.FromHours(1))
                {
                    await PurgeProcessedMessagesAsync(cancellationToken: stoppingToken);
                    _lastPurgeCheckUtc = DateTime.UtcNow;
                }

                await timer.WaitForNextTickAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[OutboxProcessor] Background Service deteniéndose de manera controlada.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OutboxProcessor] Error no controlado en ciclo de polling del Outbox.");
        }
    }

    /// <summary>
    /// Purga de forma defensiva y en lotes de 1.000 registros los mensajes de Outbox con estado 'Processed'
    /// que superen la retención configurada (por defecto 7 días).
    /// IMPORTANTE: Jamás purga mensajes en estado 'Pending', 'Failed' ni 'DeadLetter'.
    /// </summary>
    public async Task<int> PurgeProcessedMessagesAsync(DateTime? cutoffDate = null, CancellationToken cancellationToken = default)
    {
        var cutoff = cutoffDate ?? DateTime.UtcNow.AddDays(-7);
        int totalPurged = 0;
        DateTime? oldestPurged = null;

        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SalesDbContext>();

            using var cycleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cycleCts.CancelAfter(TimeSpan.FromMinutes(2));

            var targetQuery = dbContext.OutboxMessages.AsNoTracking()
                .Where(m => m.Status == "Processed" && m.ProcessedAtUtc.HasValue && m.ProcessedAtUtc.Value < cutoff);

            int pendingPurgeCount = await targetQuery.CountAsync(cycleCts.Token);
            if (pendingPurgeCount == 0) return 0;

            oldestPurged = await targetQuery.MinAsync(m => m.ProcessedAtUtc, cycleCts.Token);

            while (!cycleCts.Token.IsCancellationRequested)
            {
                int deleted = 0;
                if (dbContext.Database.IsNpgsql())
                {
                    deleted = await dbContext.Database.ExecuteSqlRawAsync(
                        "DELETE FROM \"OutboxMessages\" WHERE \"Id\" IN (SELECT \"Id\" FROM \"OutboxMessages\" WHERE \"Status\" = 'Processed' AND \"ProcessedAtUtc\" < {0} LIMIT 1000);",
                        new object[] { cutoff },
                        cycleCts.Token);
                }
                else
                {
                    var batch = await targetQuery.OrderBy(m => m.ProcessedAtUtc).Take(1000).Select(m => m.Id).ToListAsync(cycleCts.Token);
                    if (batch.Count == 0) break;
                    
                    var toDelete = await dbContext.OutboxMessages.Where(m => batch.Contains(m.Id)).ToListAsync(cycleCts.Token);
                    dbContext.OutboxMessages.RemoveRange(toDelete);
                    deleted = await dbContext.SaveChangesAsync(cycleCts.Token);
                }

                if (deleted == 0) break;
                totalPurged += deleted;
                await Task.Delay(100, cycleCts.Token);
            }

            if (totalPurged > 0)
            {
                _logger.LogInformation("[OutboxProcessor] Purga completada exitosamente. Total mensajes eliminados: {TotalPurged}. Mensaje más antiguo: {OldestPurged:O}.", totalPurged, oldestPurged);
                Core.Logging.AppLogger.LogSecurityAudit($"[OUTBOX_PURGE] Se purgaron {totalPurged} mensajes procesados de Outbox anteriores a {cutoff:O}. Fecha de mensaje más antiguo: {oldestPurged:O}.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("[OutboxProcessor] Ciclo de purga de Outbox alcanzó el timeout defensivo de 2 minutos. Total purgado: {TotalPurged}.", totalPurged);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[OutboxProcessor] Error inesperado durante la purga de mensajes procesados de Outbox.");
        }

return totalPurged;
    }


    public async Task ProcessPendingMessagesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var scope = _serviceProvider.CreateScope();
            var dbContext = scope.ServiceProvider.GetRequiredService<SalesDbContext>();
            var hubContext = scope.ServiceProvider.GetService<IHubContext<ExchangeRateHub>>();

            List<OutboxMessage> messages;

            if (dbContext.Database.IsNpgsql())
            {
                if (dbContext.Database.CurrentTransaction is not null)
                    throw new InvalidOperationException("[OutboxProcessor] No se admite una transacción ya abierta al seleccionar mensajes pendientes.");

                await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);

                messages = await dbContext.OutboxMessages
                    .FromSqlRaw("SELECT * FROM \"OutboxMessages\" WHERE \"Status\" = 'Pending' AND \"NextRetryUtc\" <= NOW() ORDER BY \"CreatedAtUtc\" LIMIT 20 FOR UPDATE SKIP LOCKED")
                    .ToListAsync(cancellationToken);

                if (!messages.Any())
                {
                    await tx.RollbackAsync(cancellationToken);
                    return;
                }

                foreach (var message in messages)
                {
                    try
                    {
                        await DispatchMessageAsync(message, hubContext, cancellationToken);

                        message.DispatchedAtUtc = DateTime.UtcNow;
                        message.Status = "Processed";
                        message.ProcessedAtUtc = DateTime.UtcNow;
                        message.ErrorMessage = null;
                    }
                    catch (Exception ex)
                    {
                        message.RetryCount++;

                        if (message.RetryCount < 5)
                        {
                            var delaySeconds = Math.Pow(2, message.RetryCount);
                            message.NextRetryUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
                            message.ErrorMessage = ex.Message;
                            _logger.LogWarning(ex, "[OutboxProcessor] Error al despachar mensaje {MessageId} (Intento {Attempt}/5). Próximo reintento en {Delay}s.",
                                message.Id, message.RetryCount, delaySeconds);
                        }
                        else
                        {
                            message.Status = "DeadLetter";
                            message.ErrorMessage = $"Falló tras 5 intentos: {ex.Message}";
                            _logger.LogCritical(ex, "[OutboxProcessor] CRÍTICO: Mensaje {MessageId} ({EventType}) movido a DeadLetter tras 5 intentos fallidos.",
                                message.Id, message.EventType);
                        }
                    }
                }

                await dbContext.SaveChangesAsync(cancellationToken);
                await tx.CommitAsync(cancellationToken);
                return;
            }
            else
            {
                var now = DateTime.UtcNow;
                messages = await dbContext.OutboxMessages
                    .Where(m => m.Status == "Pending" && m.NextRetryUtc <= now)
                    .OrderBy(m => m.CreatedAtUtc)
                    .Take(20)
                    .ToListAsync(cancellationToken);
            }

            if (!messages.Any()) return;

            foreach (var message in messages)
            {
                try
                {
                    await DispatchMessageAsync(message, hubContext, cancellationToken);

                    message.DispatchedAtUtc = DateTime.UtcNow;
                    message.Status = "Processed";
                    message.ProcessedAtUtc = DateTime.UtcNow;
                    message.ErrorMessage = null;
                }
                catch (Exception ex)
                {
                    message.RetryCount++;

                    if (message.RetryCount < 5)
                    {
                        var delaySeconds = Math.Pow(2, message.RetryCount);
                        message.NextRetryUtc = DateTime.UtcNow.AddSeconds(delaySeconds);
                        message.ErrorMessage = ex.Message;
                        _logger.LogWarning(ex, "[OutboxProcessor] Error al despachar mensaje {MessageId} (Intento {Attempt}/5). Próximo reintento en {Delay}s.",
                            message.Id, message.RetryCount, delaySeconds);
                    }
                    else
                    {
                        message.Status = "DeadLetter";
                        message.ErrorMessage = $"Falló tras 5 intentos: {ex.Message}";
                        _logger.LogCritical(ex, "[OutboxProcessor] CRÍTICO: Mensaje {MessageId} ({EventType}) movido a DeadLetter tras 5 intentos fallidos.",
                            message.Id, message.EventType);
                    }
                }
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[OutboxProcessor] Error al procesar lote de mensajes Outbox: {Message}", ex.Message);
        }
    }

    private async Task DispatchMessageAsync(OutboxMessage message, IHubContext<ExchangeRateHub>? hubContext, CancellationToken cancellationToken)
    {
        switch (message.EventType)
        {
            case "SaleCompleted":
                using (var doc = JsonDocument.Parse(message.Payload))
                {
                    if (hubContext != null)
                    {
                        if (doc.RootElement.TryGetProperty("SaleId", out var saleIdProp))
                        {
                            int saleId = saleIdProp.GetInt32();
                            await hubContext.Clients.All.SendAsync("ReceiveSaleCompleted", saleId, cancellationToken);
                        }

                        await hubContext.Clients.All.SendAsync("OnHoldSalesUpdated", cancellationToken);
                    }
                }
                break;

            default:
                _logger.LogInformation("[OutboxProcessor] EventType desconocido '{EventType}' para mensaje {MessageId}. Marcando como procesado.", message.EventType, message.Id);
                break;
        }
    }
}
