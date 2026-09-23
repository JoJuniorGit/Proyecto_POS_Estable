using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Services;
using Core.Logging;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sales.Module.Receipts;

namespace Backend.API.Jobs;

public class ReceiptPrintBackgroundService : BackgroundService
{
    private readonly ChannelReceiptPrintQueue _queue;
    private readonly IReceiptDocumentRenderer _renderer;
    private readonly ILogger<ReceiptPrintBackgroundService> _logger;

    public ReceiptPrintBackgroundService(
        ChannelReceiptPrintQueue queue,
        IReceiptDocumentRenderer renderer,
        ILogger<ReceiptPrintBackgroundService> logger)
    {
        _queue = queue;
        _renderer = renderer;
        _logger = logger;
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
                    SaveDocument(_renderer.Render(context));
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

    private static void SaveDocument(ReceiptDocument document)
    {
        if (document.Bytes is null || document.Bytes.Length == 0)
        {
            return;
        }

        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Receipts");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, document.FileName);
        File.WriteAllBytes(path, document.Bytes);
        AppLogger.LogStart($"[ReceiptPrint] Recibo guardado: {path}");
    }
}