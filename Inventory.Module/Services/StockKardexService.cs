using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Inventory.Module.Data;
using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Inventory.Module.Services;

/// <summary>
/// 8.177-T2 (S2/S3): Centro de Auditoría de Inventario (Kardex). Consulta la unión de
/// movimientos activos + archivados (decisión L2), aplica filtros combinables, pagina
/// server-side, calcula KPIs valorizados y expone la exportación con encabezado legal
/// (ver partial <c>StockKardexService.Export.cs</c>).
/// </summary>
public partial class StockKardexService : IStockKardexService
{
    public const int DefaultPageSize = 25;
    public const int MaxPageSize = 200;

    internal const string CompanyLegalNameKey = "CompanyLegalName";
    internal const string DefaultCompanyLegalName = "COMMANDCENTER POS";

    private const string SourceActive = "Active";
    private const string SourceArchived = "Archived";

    private readonly InventoryDbContext _context;
    private readonly ISystemSettingsService _settingsService;
    private readonly ICurrentUserService? _currentUserService;
    private readonly ITimeZoneProvider _timeZoneProvider;

    public StockKardexService(
        InventoryDbContext context,
        ISystemSettingsService settingsService,
        ICurrentUserService? currentUserService = null,
        ITimeZoneProvider? timeZoneProvider = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(settingsService);

        _context = context;
        _settingsService = settingsService;
        _currentUserService = currentUserService;
        _timeZoneProvider = timeZoneProvider ?? new Core.Services.TimeZoneProvider(settingsService);
    }

    public async Task<PagedResultDto<StockKardexRowDto>> GetMovementsAsync(
        StockKardexFilterDto filter,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        filter ??= new StockKardexFilterDto();
        page = Math.Max(1, page);
        pageSize = pageSize <= 0 ? DefaultPageSize : Math.Clamp(pageSize, 1, MaxPageSize);

        var query = ApplySqlFilters(BuildUnionQuery(), filter);

        if (!RequiresPostClassification(filter))
        {
            var totalCount = await query.CountAsync(cancellationToken);
            var pageItems = await OrderQuery(query)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);

            await PopulateProductInfoAsync(pageItems, cancellationToken);
            foreach (var row in pageItems)
            {
                FinalizeRow(row);
            }

            return new PagedResultDto<StockKardexRowDto>(pageItems, totalCount, page * pageSize < totalCount);
        }

        // Fase 2 (documentada): el motivo/origen EFECTIVO puede requerir la derivación heurística
        // desde el texto libre (keywords, acentos, prefijos y sufijos), que no es traducible a SQL
        // de forma fiable ni paritaria entre InMemory y Npgsql. Con esos filtros activos se
        // materializa la unión ya acotada por SQL y se filtra/pagina en memoria; sin ellos,
        // Count/Skip/Take siguen siendo 100% server-side.
        var classified = await LoadClassifiedRowsAsync(query, filter, cancellationToken);
        var classifiedPage = classified.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new PagedResultDto<StockKardexRowDto>(classifiedPage, classified.Count, page * pageSize < classified.Count);
    }

    public async Task<StockKardexSummaryDto> GetSummaryAsync(
        StockKardexFilterDto filter,
        CancellationToken cancellationToken = default)
    {
        filter ??= new StockKardexFilterDto();
        var query = ApplySqlFilters(BuildUnionQuery(), filter);

        var rows = RequiresPostClassification(filter)
            ? await LoadClassifiedRowsAsync(query, filter, cancellationToken)
            : await LoadAllFinalizedAsync(query, cancellationToken);

        long unvalued = 0;
        decimal netVolume = 0m;
        decimal entries = 0m;
        decimal losses = 0m;

        foreach (var row in rows)
        {
            netVolume += row.QuantityChange;

            if (!row.UnitCostUSD.HasValue)
            {
                unvalued++;
                continue;
            }

            // TotalValueUSD ya viene redondeado por fila (2 decimales AwayFromZero): el KPI es
            // consistente con la suma de los valores que la UI muestra por movimiento.
            var value = row.TotalValueUSD ?? 0m;
            if (row.QuantityChange > 0)
            {
                entries += value;
            }
            else if (row.QuantityChange < 0)
            {
                losses += Math.Abs(value);
            }
        }

        entries = Math.Round(entries, 2, MidpointRounding.AwayFromZero);
        losses = Math.Round(losses, 2, MidpointRounding.AwayFromZero);

        return new StockKardexSummaryDto
        {
            TotalMovements = rows.Count,
            NetVolume = netVolume,
            EntriesValueUsd = entries,
            LossesValueUsd = losses,
            NetValueUsd = entries - losses,
            UnvaluedMovements = unvalued
        };
    }

    /// <summary>
    /// 8.177-T2: proyección unificada activa + archivo. EF traduce <c>Concat</c> a UNION ALL en
    /// Npgsql y a una concatenación LINQ en InMemory. El archivo no tiene navegación a Product:
    /// SKU/nombre se resuelven después con una única consulta de lookup por página/lote.
    /// </summary>
    private IQueryable<StockKardexRowDto> BuildUnionQuery()
    {
        var active = _context.StockMovements
            .AsNoTracking()
            .Select(m => new StockKardexRowDto
            {
                Source = SourceActive,
                MovementId = m.Id,
                ArchivedId = null,
                OriginalMovementId = null,
                ProductId = m.ProductId,
                MovementDateUtc = m.MovementDate,
                QuantityChange = m.QuantityChange,
                StockBefore = m.NewStockLevel - m.QuantityChange,
                NewStockLevel = m.NewStockLevel,
                UnitCostUSD = m.UnitCostUSD,
                OriginType = m.OriginType,
                ReasonCode = m.ReasonCode,
                Reason = m.Reason,
                ExecutedByUserId = m.UserId,
                AuthorizedByUserId = m.AuthorizedByUserId,
                SaleId = m.SaleId,
                SupplierInvoiceId = m.SupplierInvoiceId,
                Observation = m.Observation
            });

        var archived = _context.StockMovements_Archive
            .AsNoTracking()
            .Select(m => new StockKardexRowDto
            {
                Source = SourceArchived,
                MovementId = m.OriginalMovementId,
                ArchivedId = m.Id,
                OriginalMovementId = m.OriginalMovementId,
                ProductId = m.ProductId,
                MovementDateUtc = m.MovementDate,
                QuantityChange = m.QuantityChange,
                StockBefore = m.NewStockLevel - m.QuantityChange,
                NewStockLevel = m.NewStockLevel,
                UnitCostUSD = m.UnitCostUSD,
                OriginType = m.OriginType,
                ReasonCode = m.ReasonCode,
                Reason = m.Reason,
                ExecutedByUserId = m.UserId,
                AuthorizedByUserId = m.AuthorizedByUserId,
                SaleId = m.SaleId,
                SupplierInvoiceId = m.SupplierInvoiceId,
                Observation = m.Observation
            });

        return active.Concat(archived);
    }

    private static IQueryable<StockKardexRowDto> ApplySqlFilters(IQueryable<StockKardexRowDto> query, StockKardexFilterDto filter)
    {
        if (filter.DateFromUtc.HasValue)
        {
            query = query.Where(r => r.MovementDateUtc >= filter.DateFromUtc.Value);
        }

        if (filter.DateToUtc.HasValue)
        {
            query = query.Where(r => r.MovementDateUtc <= filter.DateToUtc.Value);
        }

        if (!string.IsNullOrWhiteSpace(filter.UserId))
        {
            var userId = filter.UserId.Trim();
            query = query.Where(r => r.ExecutedByUserId == userId || r.AuthorizedByUserId == userId);
        }

        if (filter.ProductId.HasValue)
        {
            query = query.Where(r => r.ProductId == filter.ProductId.Value);
        }

        if (filter.Direction == StockKardexDirection.In)
        {
            query = query.Where(r => r.QuantityChange > 0);
        }
        else if (filter.Direction == StockKardexDirection.Out)
        {
            query = query.Where(r => r.QuantityChange < 0);
        }

        return query;
    }

    private static bool RequiresPostClassification(StockKardexFilterDto filter) =>
        (filter.ReasonCodes?.Count ?? 0) > 0 || (filter.OriginTypes?.Count ?? 0) > 0;

    private static IOrderedQueryable<StockKardexRowDto> OrderQuery(IQueryable<StockKardexRowDto> query) =>
        query.OrderByDescending(r => r.MovementDateUtc)
            .ThenBy(r => r.Source)
            .ThenByDescending(r => r.MovementId);

    private static List<StockKardexRowDto> OrderRows(IEnumerable<StockKardexRowDto> rows) =>
        rows.OrderByDescending(r => r.MovementDateUtc)
            .ThenBy(r => r.Source, StringComparer.Ordinal)
            .ThenByDescending(r => r.MovementId)
            .ToList();

    private async Task<List<StockKardexRowDto>> LoadAllFinalizedAsync(IQueryable<StockKardexRowDto> query, CancellationToken cancellationToken)
    {
        var rows = await OrderQuery(query).ToListAsync(cancellationToken);
        await PopulateProductInfoAsync(rows, cancellationToken);
        foreach (var row in rows)
        {
            FinalizeRow(row);
        }

        return rows;
    }

    private async Task<List<StockKardexRowDto>> LoadClassifiedRowsAsync(
        IQueryable<StockKardexRowDto> query,
        StockKardexFilterDto filter,
        CancellationToken cancellationToken)
    {
        var rows = await query.ToListAsync(cancellationToken);
        await PopulateProductInfoAsync(rows, cancellationToken);

        var matches = new List<StockKardexRowDto>(rows.Count);
        foreach (var row in rows)
        {
            FinalizeRow(row);
            if (MatchesEffectiveClassification(row, filter))
            {
                matches.Add(row);
            }
        }

        return OrderRows(matches);
    }

    private static bool MatchesEffectiveClassification(StockKardexRowDto row, StockKardexFilterDto filter)
    {
        if (filter.ReasonCodes is { Count: > 0 } reasons
            && (row.ReasonCode is null || !reasons.Contains(row.ReasonCode.Value)))
        {
            return false;
        }

        if (filter.OriginTypes is { Count: > 0 } origins
            && (row.OriginType is null || !origins.Contains(row.OriginType.Value)))
        {
            return false;
        }

        return true;
    }

    private async Task PopulateProductInfoAsync(List<StockKardexRowDto> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var productIds = rows.Select(r => r.ProductId).Distinct().ToList();
        var products = await _context.Products
            .AsNoTracking()
            .Where(p => productIds.Contains(p.Id))
            .Select(p => new { p.Id, p.SKU, p.Name })
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        foreach (var row in rows)
        {
            if (products.TryGetValue(row.ProductId, out var product))
            {
                row.SKU = product.SKU;
                row.ProductName = product.Name;
            }
        }
    }

    internal static void FinalizeRow(StockKardexRowDto row)
    {
        var (origin, reason) = StockMovementKardexClassifier.Resolve(
            row.Reason, row.SaleId, row.OriginType, row.ReasonCode);

        row.OriginType = origin;
        row.ReasonCode = reason;
        row.TotalValueUSD = row.UnitCostUSD.HasValue
            ? Math.Round(row.QuantityChange * row.UnitCostUSD.Value, 2, MidpointRounding.AwayFromZero)
            : null;
    }
}

/// <summary>
/// 8.177-T2: derivación histórica best-effort del origen/motivo para movimientos previos a
/// 8.177-T1 (campos nullable). Los valores PERSISTIDOS siempre mandan; esto solo completa nulls
/// a partir de los formatos de texto conocidos de los productores (checkout, ajuste manual,
/// importación, carga inicial y consolidación de variantes). Un texto desconocido queda sin
/// clasificar (null): la heurística no inventa clasificaciones.
/// </summary>
public static class StockMovementKardexClassifier
{
    private const string ManualPrefix = "Ajuste Manual:";
    private const string VariantPrefix = "Variante:";

    public static (StockMovementOriginType? Origin, StockMovementReason? Reason) Resolve(
        string? reason,
        int? saleId,
        StockMovementOriginType? persistedOrigin,
        StockMovementReason? persistedReason)
    {
        if (persistedOrigin.HasValue && persistedReason.HasValue)
        {
            return (persistedOrigin, persistedReason);
        }

        var derived = Derive(reason, saleId);
        return (persistedOrigin ?? derived.Origin, persistedReason ?? derived.Reason);
    }

    public static (StockMovementOriginType? Origin, StockMovementReason? Reason) Derive(string? reason, int? saleId)
    {
        var text = (reason ?? string.Empty).Trim();

        // "Variante: {nombre} (SKU..., Factor...) | {motivo original}": la clasificación mira el
        // motivo posterior al separador, que describe la operación real del consolidado.
        if (text.StartsWith(VariantPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var separatorIndex = text.IndexOf(" | ", StringComparison.Ordinal);
            if (separatorIndex >= 0)
            {
                text = text[(separatorIndex + 3)..].Trim();
            }
        }

        if (saleId.HasValue)
        {
            return ContainsAny(text, "devol", "anul")
                ? (StockMovementOriginType.System, StockMovementReason.Return)
                : (StockMovementOriginType.System, StockMovementReason.Sale);
        }

        // 8.177-T2 (fix verificación): el prefijo "Ajuste Manual:" es la señal más específica y se
        // evalúa ANTES de los keywords genéricos; si no, textos como "Ajuste Manual: merma
        // importante" clasificarían como Import por el substring "importa".
        if (text.StartsWith(ManualPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return (StockMovementOriginType.Manual, ClassifyManualSuffix(text[ManualPrefix.Length..]));
        }

        if (ContainsAny(text, "carga inicial"))
        {
            return (StockMovementOriginType.System, StockMovementReason.InitialLoad);
        }

        // "importaci" (no "importa") para no matchear palabras como "importante".
        if (ContainsAny(text, "importaci"))
        {
            return (StockMovementOriginType.Import, StockMovementReason.ImportAdjustment);
        }

        if (ContainsAny(text, "devol", "anul"))
        {
            return (StockMovementOriginType.System, StockMovementReason.Return);
        }

        if (text.StartsWith("sale #", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("venta", StringComparison.OrdinalIgnoreCase))
        {
            return (StockMovementOriginType.System, StockMovementReason.Sale);
        }

        return (null, null);
    }

    private static StockMovementReason? ClassifyManualSuffix(string suffix)
    {
        if (ContainsAny(suffix, "merma", "dañ", "deterior"))
        {
            return StockMovementReason.Damage;
        }

        if (ContainsAny(suffix, "venc"))
        {
            return StockMovementReason.Expiry;
        }

        if (ContainsAny(suffix, "conteo", "inventario"))
        {
            return StockMovementReason.PhysicalCountCorrection;
        }

        if (ContainsAny(suffix, "devol"))
        {
            return StockMovementReason.Return;
        }

        if (ContainsWholeWord(suffix, "compra"))
        {
            return StockMovementReason.PurchaseEntry;
        }

        return null;
    }

    /// <summary>
    /// 8.177-T2 (fix verificación): match por palabra completa (evita que "compra" clasifique
    /// "comprador"). Comparación ordinal sin distinguir mayúsculas.
    /// </summary>
    private static bool ContainsWholeWord(string text, string word)
    {
        var index = text.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var beforeIsLetter = index > 0 && char.IsLetter(text[index - 1]);
            var afterIndex = index + word.Length;
            var afterIsLetter = afterIndex < text.Length && char.IsLetter(text[afterIndex]);
            if (!beforeIsLetter && !afterIsLetter)
            {
                return true;
            }

            index = text.IndexOf(word, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool ContainsAny(string text, params string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
