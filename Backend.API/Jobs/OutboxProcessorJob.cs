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
                messages = await dbContext.OutboxMessages
                    .FromSqlRaw("SELECT * FROM \"OutboxMessages\" WHERE \"Status\" = 'Pending' AND \"NextRetryUtc\" <= NOW() ORDER BY \"CreatedAtUtc\" LIMIT 20 FOR UPDATE SKIP LOCKED")
                    .ToListAsync(cancellationToken);
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
