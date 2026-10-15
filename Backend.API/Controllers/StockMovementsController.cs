using Backend.API.Attributes;
using Core.DTOs;
using Core.Entities;
using Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

/// <summary>
/// 8.177-T2 (S2/S3): API de consulta del Kardex de Inventario — movimientos unificados
/// (activos + archivo), KPIs y exportación CSV/XLSX/PDF con encabezado legal. Solo lectura:
/// no expone endpoints de escritura.
/// </summary>
[ApiController]
[Authorize(Roles = "Admin,Manager")]
[RequireSecurityStampValidation]
[Route("api/stock-movements")]
public sealed class StockMovementsController : ControllerBase
{
    private const int DefaultPageSize = 25;

    private static readonly Dictionary<string, (string ContentType, string Extension)> ExportFormats =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["csv"] = ("text/csv; charset=utf-8", "csv"),
            ["xlsx"] = ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx"),
            ["pdf"] = ("application/pdf", "pdf")
        };

    private readonly IStockKardexService _stockKardexService;

    public StockMovementsController(IStockKardexService stockKardexService)
    {
        ArgumentNullException.ThrowIfNull(stockKardexService);
        _stockKardexService = stockKardexService;
    }

    [HttpGet]
    public async Task<ActionResult<PagedResultDto<StockKardexRowDto>>> GetMovementsAsync(
        [FromQuery] DateTime? dateFromUtc = null,
        [FromQuery] DateTime? dateToUtc = null,
        [FromQuery] string? userId = null,
        [FromQuery] int? productId = null,
        [FromQuery] string? reasonCodes = null,
        [FromQuery] string? originTypes = null,
        [FromQuery] StockKardexDirection direction = StockKardexDirection.All,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildFilter(dateFromUtc, dateToUtc, userId, productId, reasonCodes, originTypes, direction, out var filter, out var error))
        {
            return this.ApiBadRequest(error);
        }

        var result = await _stockKardexService.GetMovementsAsync(filter!, page, pageSize, cancellationToken);
        return Ok(result);
    }

    [HttpGet("summary")]
    public async Task<ActionResult<StockKardexSummaryDto>> GetSummaryAsync(
        [FromQuery] DateTime? dateFromUtc = null,
        [FromQuery] DateTime? dateToUtc = null,
        [FromQuery] string? userId = null,
        [FromQuery] int? productId = null,
        [FromQuery] string? reasonCodes = null,
        [FromQuery] string? originTypes = null,
        [FromQuery] StockKardexDirection direction = StockKardexDirection.All,
        CancellationToken cancellationToken = default)
    {
        if (!TryBuildFilter(dateFromUtc, dateToUtc, userId, productId, reasonCodes, originTypes, direction, out var filter, out var error))
        {
            return this.ApiBadRequest(error);
        }

        var summary = await _stockKardexService.GetSummaryAsync(filter!, cancellationToken);
        return Ok(summary);
    }

    [HttpGet("export")]
    public async Task<IActionResult> ExportAsync(
        [FromQuery] string format = "csv",
        [FromQuery] DateTime? dateFromUtc = null,
        [FromQuery] DateTime? dateToUtc = null,
        [FromQuery] string? userId = null,
        [FromQuery] int? productId = null,
        [FromQuery] string? reasonCodes = null,
        [FromQuery] string? originTypes = null,
        [FromQuery] StockKardexDirection direction = StockKardexDirection.All,
        CancellationToken cancellationToken = default)
    {
        var normalizedFormat = (format ?? string.Empty).Trim().ToLowerInvariant();
        if (!ExportFormats.TryGetValue(normalizedFormat, out var formatInfo))
        {
            return this.ApiBadRequest("Formato de exportación inválido. Formatos válidos: csv, xlsx, pdf.");
        }

        if (!TryBuildFilter(dateFromUtc, dateToUtc, userId, productId, reasonCodes, originTypes, direction, out var filter, out var error))
        {
            return this.ApiBadRequest(error);
        }

        var bytes = await _stockKardexService.ExportAsync(filter!, normalizedFormat, cancellationToken);
        var fileName = $"kardex-{DateTime.UtcNow:yyyyMMdd-HHmmss}.{formatInfo.Extension}";
        return File(bytes, formatInfo.ContentType, fileName);
    }

    internal static bool TryBuildFilter(
        DateTime? dateFromUtc,
        DateTime? dateToUtc,
        string? userId,
        int? productId,
        string? reasonCodes,
        string? originTypes,
        StockKardexDirection direction,
        out StockKardexFilterDto? filter,
        out string? error)
    {
        filter = null;
        error = null;

        if (!TryParseEnumList(reasonCodes, out IReadOnlyList<StockMovementReason>? parsedReasons))
        {
            error = "El parámetro reasonCodes contiene valores inválidos.";
            return false;
        }

        if (!TryParseEnumList(originTypes, out IReadOnlyList<StockMovementOriginType>? parsedOrigins))
        {
            error = "El parámetro originTypes contiene valores inválidos.";
            return false;
        }

        if (!Enum.IsDefined(typeof(StockKardexDirection), direction))
        {
            error = "El parámetro direction debe ser 0 (All), 1 (In) o 2 (Out).";
            return false;
        }

        filter = new StockKardexFilterDto
        {
            DateFromUtc = dateFromUtc,
            DateToUtc = dateToUtc,
            UserId = string.IsNullOrWhiteSpace(userId) ? null : userId.Trim(),
            ProductId = productId,
            ReasonCodes = parsedReasons,
            OriginTypes = parsedOrigins,
            Direction = direction
        };

        return true;
    }

    /// <summary>
    /// Acepta listas separadas por coma de nombres de enum ("Damage,Return") o de valores
    /// numéricos ("2,5"), tolerando la mezcla. Un token inválido rechaza el request completo.
    /// </summary>
    internal static bool TryParseEnumList<TEnum>(string? raw, out IReadOnlyList<TEnum>? values)
        where TEnum : struct, Enum
    {
        values = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        var parsed = new List<TEnum>();
        foreach (var token in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            {
                var candidate = (TEnum)Enum.ToObject(typeof(TEnum), numeric);
                if (!Enum.IsDefined(typeof(TEnum), candidate))
                {
                    return false;
                }

                parsed.Add(candidate);
            }
            else if (Enum.TryParse<TEnum>(token, ignoreCase: true, out var named))
            {
                parsed.Add(named);
            }
            else
            {
                return false;
            }
        }

        values = parsed.Count == 0 ? null : parsed;
        return true;
    }
}
