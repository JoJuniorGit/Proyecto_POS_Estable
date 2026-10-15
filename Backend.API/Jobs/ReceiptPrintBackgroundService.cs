using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Services;
using Core.Helpers;
using Core.Interfaces;
using Core.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sales.Module.Receipts;

namespace Backend.API.Jobs;

public class ReceiptPrintBackgroundService : BackgroundService
{
    private readonly ChannelReceiptPrintQueue _queue;
    private readonly IReceiptDocumentRenderer _renderer;
    private readonly ILogger<ReceiptPrintBackgroundService> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public ReceiptPrintBackgroundService(
        ChannelReceiptPrintQueue queue,
        IReceiptDocumentRenderer renderer,
        ILogger<ReceiptPrintBackgroundService> logger,
        IServiceScopeFactory scopeFactory)
    {
        _queue = queue;
        _renderer = renderer;
        _logger = logger;
        _scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            await foreach (var context in _queue.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    var format = await ResolveCurrencyFormatAsync();
                    await SaveDocumentAsync(_renderer.Render(context, format), stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[ReceiptPrint] Error al renderizar/guardar el recibo de la venta {SaleId}.", context.SaleId);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[ReceiptPrint] Servicio de impresion de recibos deteniendose.");
        }
    }

    // 8.143: el recibo encolado también honra el ajuste CurrencyFormat. ISystemSettingsService es
    // Scoped y este hosted service es Singleton: se resuelve en un scope propio por documento.
    private async Task<MoneyDisplayFormat> ResolveCurrencyFormatAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var settingsService = scope.ServiceProvider.GetRequiredService<ISystemSettingsService>();
            return MoneyFormat.ParseFormat(await settingsService.GetSettingAsync("CurrencyFormat"));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[ReceiptPrint] Error al leer CurrencyFormat; usando formato por defecto.");
            return MoneyDisplayFormat.Venezuelan;
        }
    }

    private static async Task SaveDocumentAsync(ReceiptDocument document, CancellationToken cancellationToken)
    {
        if (document.Bytes is null || document.Bytes.Length == 0)
        {
            return;
        }

        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Receipts");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, document.FileName);
        await File.WriteAllBytesAsync(path, document.Bytes, cancellationToken);
        AppLogger.LogStart($"[ReceiptPrint] Recibo guardado: {path}");
    }
}