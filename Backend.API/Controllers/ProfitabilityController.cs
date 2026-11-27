using Backend.API.Attributes;
using Backend.API.Contracts;
using Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sales.Module.DTOs.Profitability;
using Sales.Module.Interfaces;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace Backend.API.Controllers;

/// <summary>
/// Paso 14 (F3-S1): API REST del Estado de Resultados. Lectura exclusiva para Admin/Manager;
/// el cajero y los no autenticados quedan fuera del reporte de rentabilidad.
/// </summary>
[ApiController]
[Authorize(Policy = "RequireAdminOrManager")]
[RequireSecurityStampValidation]
[Route("api/profitability")]
public sealed class ProfitabilityController : ControllerBase
{
    private readonly IProfitabilityService _profitabilityService;
    private readonly IProfitabilityExportService _exportService;
    private readonly ISystemSettingsService _settingsService;

    public ProfitabilityController(
        IProfitabilityService profitabilityService,
        IProfitabilityExportService exportService,
        ISystemSettingsService settingsService)
    {
        ArgumentNullException.ThrowIfNull(profitabilityService);
        ArgumentNullException.ThrowIfNull(exportService);
        ArgumentNullException.ThrowIfNull(settingsService);

        _profitabilityService = profitabilityService;
        _exportService = exportService;
        _settingsService = settingsService;
    }

    [HttpGet("summary")]
    public async Task<ActionResult<ProfitabilitySummaryDto>> GetSummaryAsync(
        [FromQuery] ProfitabilityFilterRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var summary = await _profitabilityService.GetSummaryAsync(
                request.ToFilter(),
                cancellationToken);
            return Ok(summary);
        }
        catch (ArgumentException exception)
        {
            return this.ApiBadRequest(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return this.ApiBadRequest(exception.Message);
        }
    }

    [HttpGet("breakdown")]
    public async Task<ActionResult<ProfitabilityBreakdownDto>> GetBreakdownAsync(
        [FromQuery] ProfitabilityFilterRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var breakdown = await _profitabilityService.GetBreakdownAsync(
                request.ToFilter(),
                cancellationToken);
            return Ok(breakdown);
        }
        catch (ArgumentException exception)
        {
            return this.ApiBadRequest(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return this.ApiBadRequest(exception.Message);
        }
    }

    /// <summary>
    /// Paso 14 (F3-S3): exportación unificada del Estado de Resultados en CSV, XLSX o PDF. Los
    /// formatos no soportados responden 400 antes de resolver el resumen.
    /// </summary>
    [HttpGet("export")]
    public async Task<IActionResult> ExportAsync(
        [FromQuery] ProfitabilityFilterRequest request,
        [FromQuery] string format = "csv",
        CancellationToken cancellationToken = default)
    {
        var normalizedFormat = (format ?? string.Empty).Trim().ToLowerInvariant();
        if (normalizedFormat is not ("csv" or "xlsx" or "pdf"))
        {
            return this.ApiBadRequest("Formato de exportación inválido. Formatos válidos: csv, xlsx, pdf.");
        }

        try
        {
            request ??= new ProfitabilityFilterRequest();
            var exportContext = new ProfitabilityExportContext
            {
                CompanyName = await ResolveCompanyNameAsync(),
                ExportedBy = User.Identity?.Name ?? "Usuario",
                FilterSummary = BuildFilterSummary(request),
                GeneratedAtUtc = DateTime.UtcNow
            };

            var file = await _exportService.ExportAsync(
                request.ToFilter(),
                normalizedFormat,
                exportContext,
                cancellationToken);

            return File(file.Content, file.ContentType, file.FileName);
        }
        catch (NotSupportedException exception)
        {
            return this.ApiBadRequest(exception.Message);
        }
        catch (ArgumentException exception)
        {
            return this.ApiBadRequest(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return this.ApiBadRequest(exception.Message);
        }
    }

    private async Task<string> ResolveCompanyNameAsync()
    {
        var configured = await _settingsService.GetSettingAsync("CompanyName");
        if (string.IsNullOrWhiteSpace(configured))
        {
            // The settings screen persists the legal/commercial name under CompanyLegalName (same
            // key used by the Kardex export); CompanyName stays as the preferred override.
            configured = await _settingsService.GetSettingAsync("CompanyLegalName");
        }

        return string.IsNullOrWhiteSpace(configured) ? "CommandCenter POS" : configured.Trim();
    }

    private static string BuildFilterSummary(ProfitabilityFilterRequest request)
    {
        var parts = new List<string> { $"Período: {request.Preset}" };
        if (request.FromDate.HasValue)
        {
            parts.Add($"Desde: {request.FromDate.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}");
        }

        if (request.ToDate.HasValue)
        {
            parts.Add($"Hasta: {request.ToDate.Value.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}");
        }

        if (request.CategoryIds is { Length: > 0 })
        {
            parts.Add($"Categorías: {string.Join(", ", request.CategoryIds)}");
            parts.Add(request.IncludeCategorySubtree ? "Subárbol: sí" : "Subárbol: no");
        }

        if (request.ProductIds is { Length: > 0 })
        {
            parts.Add($"Productos: {string.Join(", ", request.ProductIds)}");
        }

        if (request.PaymentMethodIds is { Length: > 0 })
        {
            parts.Add($"Medios de pago: {string.Join(", ", request.PaymentMethodIds)}");
        }

        if (request.CashierId.HasValue)
        {
            parts.Add($"Cajero: {request.CashierId.Value.ToString(CultureInfo.InvariantCulture)}");
        }

        if (request.SaleType.HasValue)
        {
            parts.Add($"Tipo: {request.SaleType.Value}");
        }

        if (!string.IsNullOrWhiteSpace(request.PriceListType))
        {
            parts.Add($"Lista: {request.PriceListType.Trim()}");
        }

        return string.Join(" | ", parts);
    }
}
