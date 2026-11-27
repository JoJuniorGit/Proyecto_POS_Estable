using System;
using System.Threading;
using System.Threading.Tasks;
using Backend.API.Attributes;
using Core.DTOs;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sales.Module.Interfaces;

namespace Backend.API.Controllers;

/// <summary>
/// CxC (historial realizado): subseccion Deudas Pagadas separada de la cartera operativa.
/// La tasa y la moneda de cada abono se leen de los snapshots persistidos; el servidor nunca
/// reproyecta el historial a la tasa del dia.
/// </summary>
[RequireSecurityStampValidation]
[Authorize(Roles = "Admin,Manager,Cashier")]
[ApiController]
[Route("api/receivables")]
public class ReceivablesController : ControllerBase
{
    /// <summary>Listado paginado de obligaciones 100% pagadas con KPIs del periodo filtrado.</summary>
    [HttpGet("paid")]
    [ProducesResponseType(typeof(PaidReceivablesListDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<PaidReceivablesListDto>> GetPaidAsync(
        [FromQuery] PaidReceivablesFilterDto filter,
        [FromServices] ICreditService creditService,
        CancellationToken cancellationToken = default)
    {
        var result = await creditService.GetPaidReceivablesAsync(
            filter ?? new PaidReceivablesFilterDto(),
            cancellationToken);
        return Ok(result);
    }

    /// <summary>Bitacora plana paginada de abonos de deudas pagadas, con sus imputaciones.</summary>
    [HttpGet("paid/payments")]
    [ProducesResponseType(typeof(PaidReceivablePaymentsListDto), StatusCodes.Status200OK)]
    public async Task<ActionResult<PaidReceivablePaymentsListDto>> GetPaidPaymentsAsync(
        [FromQuery] PaidReceivablesFilterDto filter,
        [FromServices] ICreditService creditService,
        CancellationToken cancellationToken = default)
    {
        var result = await creditService.GetPaidReceivablePaymentsAsync(
            filter ?? new PaidReceivablesFilterDto(),
            cancellationToken);
        return Ok(result);
    }

    /// <summary>Exportacion server-side del historial filtrado (CSV, XLSX o PDF).</summary>
    [HttpGet("paid/export")]
    public async Task<IActionResult> ExportPaidAsync(
        [FromQuery] PaidReceivablesFilterDto filter,
        [FromQuery] string format = "csv",
        [FromQuery] string mode = "invoice",
        [FromServices] IReceivablesExportService exportService = null!,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var file = await exportService.ExportPaidAsync(
                filter ?? new PaidReceivablesFilterDto(),
                mode,
                format,
                cancellationToken);
            return File(file.Content, file.ContentType, file.FileName);
        }
        catch (ArgumentException ex)
        {
            return this.ApiBadRequest(ex.Message);
        }
    }

    /// <summary>Constancia de cancelacion / estado de cuenta saldado de una obligacion.</summary>
    [HttpGet("{obligationId:int}/payments/pdf")]
    public async Task<IActionResult> ExportStatementAsync(
        int obligationId,
        [FromServices] IReceivablesExportService exportService,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var file = await exportService.ExportStatementAsync(obligationId, cancellationToken);
            return File(file.Content, file.ContentType, file.FileName);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }

    /// <summary>
    /// Cronologia ascendente de abonos de una obligacion. Sirve para deudas pagadas y parciales;
    /// conserva la tasa historica de cada abono y el saldo exacto resultante.
    /// </summary>
    [HttpGet("{obligationId:int}/payments")]
    [ProducesResponseType(typeof(ReceivablePaymentTimelineDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ReceivablePaymentTimelineDto>> GetPaymentsAsync(
        int obligationId,
        [FromServices] ICreditService creditService,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await creditService.GetReceivablePaymentTimelineAsync(
                obligationId,
                cancellationToken);
            return Ok(result);
        }
        catch (System.Collections.Generic.KeyNotFoundException ex)
        {
            return this.ApiNotFound(ex.Message);
        }
    }
}
