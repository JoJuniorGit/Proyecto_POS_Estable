using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Hubs;
using Core.Entities;
using Core.Logging;
using Inventory.Module.Data;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Sales.Module.Interfaces;

namespace Backend.API.Services;

/// <summary>8.7-M3: único camino de escritura de la tasa BCV del día. Lo usan el endpoint manual
/// (ExchangeRateController.UpsertRate), la sincronización manual (SyncBcv) y el job periódico
/// (BcvExchangeRateJob), eliminando la duplicación de upsert/invalidación/recálculo/broadcast.</summary>
public class ExchangeRateWriteService : IExchangeRateWriteService
{
    private readonly InventoryDbContext _context;
    private readonly Core.Interfaces.IInventoryService _inventoryService;
    private readonly ISalesService _salesService;
    private readonly IHubContext<ExchangeRateHub> _hubContext;

    public ExchangeRateWriteService(
        InventoryDbContext context,
        Core.Interfaces.IInventoryService inventoryService,
        ISalesService salesService,
        IHubContext<ExchangeRateHub> hubContext)
    {
        _context = context;
        _inventoryService = inventoryService;
        _salesService = salesService;
        _hubContext = hubContext;
    }

    public async Task<bool> UpsertTodayRateAsync(decimal roundedRate, CancellationToken cancellationToken = default)
    {
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        var existing = await _context.ExchangeRateHistory
            .FirstOrDefaultAsync(r => r.Date == today, cancellationToken);

        bool changed;
        if (existing != null)
        {
            changed = existing.Rate != roundedRate;
            existing.Rate = roundedRate;
            existing.UpdatedAt = DateTime.UtcNow;
        }
        else
        {
            _context.ExchangeRateHistory.Add(new ExchangeRateHistory
            {
                Date = today,
                Rate = roundedRate,
                UpdatedAt = DateTime.UtcNow
            });
            changed = true;
        }

        // Sin cambio real: no se escribe ni se difunde (evita recálculos OnHold innecesarios).
        if (!changed)
        {
            return false;
        }

        await _context.SaveChangesAsync(cancellationToken);

        _inventoryService.InvalidateTodayExchangeRateCache();

        await _salesService.RecalculateOnHoldSalesAsync(roundedRate);

        await _hubContext.Clients.All.SendAsync("ReceiveRateUpdate", roundedRate, cancellationToken);
        await _hubContext.Clients.All.SendAsync("OnHoldSalesUpdated", cancellationToken);

        return true;
    }
}

/// <summary>8.7-M3: resolución compartida de la tasa efectiva del día (BCV hoy -> último BCV histórico ->
/// tasa de apertura de sesión activa -> 0/NA). Reemplaza los métodos privados duplicados de
/// DailyClosureController y ShiftsController.</summary>
public static class ExchangeRateResolver
{
    public static async Task<decimal> ReadEffectiveTodayRateAsync(
        InventoryDbContext inventoryContext,
        ICashDrawerService cashDrawerService)
    {
        var today = Core.Helpers.TimeZoneHelper.GetVenezuelaDate();
        var record = await inventoryContext.ExchangeRateHistory
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Date == today);

        if (record == null)
        {
            record = await inventoryContext.ExchangeRateHistory
                .AsNoTracking()
                .Where(r => r.Date <= today)
                .OrderByDescending(r => r.Date)
                .FirstOrDefaultAsync();
        }

        if (record != null && record.Rate > 0)
            return record.Rate;

        // Fallback a la tasa de apertura de la sesión activa para evitar distorsiones con 1.0 (8.2-M2)
        var activeSession = await cashDrawerService.GetActiveSessionAsync();
        if (activeSession != null && activeSession.OpeningExchangeRate > 0)
            return activeSession.OpeningExchangeRate;

        // 8.2-M2: Tasa NA explícita (0) en lugar de un fallback silencioso 1.0.
        // Los cierres sin tasa BCV del día se bloquean con error claro.
        return 0m;
    }
}