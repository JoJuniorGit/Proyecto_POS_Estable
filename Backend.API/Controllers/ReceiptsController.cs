using System;
using System.Threading;
using System.Threading.Tasks;
using Core.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Sales.Module.Receipts;

namespace Backend.API.Controllers;

[Authorize]
[ApiController]
[Route("api/sales")]
public class ReceiptsController : ControllerBase
{
    private readonly ISalesReceiptService _receiptService;
    private readonly IReceiptDocumentRenderer _renderer;
    private readonly Core.Interfaces.ICurrentUserService _currentUserService;
    private readonly Core.Interfaces.ISystemSettingsService _settingsService;

    public ReceiptsController(ISalesReceiptService receiptService, IReceiptDocumentRenderer renderer, Core.Interfaces.ICurrentUserService currentUserService, Core.Interfaces.ISystemSettingsService settingsService)
    {
        _receiptService = receiptService;
        _renderer = renderer;
        _currentUserService = currentUserService;
        _settingsService = settingsService;
    }

    [HttpGet("{saleId:int}/receipt")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<IActionResult> GetReceiptAsync(int saleId, CancellationToken cancellationToken = default)
    {
        var data = await _receiptService.GetReceiptDataAsync(saleId, cancellationToken);
        if (data == null)
        {
            return this.ApiNotFound("La venta solicitada no existe.");
        }

        var (context, cashierId, hasInvoice) = data;

        bool isElevated = User.IsInRole("Admin") || User.IsInRole("Manager");
        if (!isElevated)
        {
            if (string.IsNullOrEmpty(_currentUserService.UserId) || !int.TryParse(_currentUserService.UserId, out int uid) || cashierId != uid)
            {
                return this.ApiForbidden("Acceso denegado: no tiene permisos para descargar el recibo de esta venta.");
            }
        }

        if (!hasInvoice)
        {
            return this.ApiBadRequest("La venta aún no está completada y no tiene recibo.");
        }

        if (context == null)
        {
            return this.ApiNotFound("No se pudo generar el contexto de comprobante.");
        }

        // 8.143: el recibo honra el ajuste de formato de moneda; best-effort (si falla, Venezuelan).
        string? currencyFormat = null;
        try
        {
            currencyFormat = await _settingsService.GetSettingAsync("CurrencyFormat");
        }
        catch (Exception ex)
        {
            Core.Logging.AppLogger.LogWarn($"No se pudo leer CurrencyFormat al generar recibo #{saleId}: {ex.Message}", nameof(ReceiptsController));
        }

        var document = _renderer.Render(context, MoneyFormat.ParseFormat(currencyFormat));
        return File(document.Bytes!, "application/pdf", document.FileName);
    }

    /// <summary>
    /// T3a (F2b, S5): comprobante de abono CxC on-demand (PDF): saldo anterior/nuevo por factura,
    /// monto abonado en USD y Bs.S a la tasa del cobro, saldo pendiente del cliente en USD con
    /// equivalente Bs.S del día y tasa del abono. El cajero solo descarga sus propios comprobantes
    /// (ReceivedByUserId del cobro); Admin/Manager tienen bypass.
    /// </summary>
    [HttpGet("credit-payments/{paymentId:int}/receipt")]
    [Authorize(Roles = "Admin,Manager,Cashier")]
    public async Task<IActionResult> GetCreditPaymentReceiptAsync(
        int paymentId,
        [FromServices] Core.Interfaces.ITodayExchangeRateProvider rateProvider,
        CancellationToken cancellationToken = default)
    {
        // Best-effort: sin tasa del día el contexto conserva la tasa del abono para la proyección Bs.S.
        decimal todayRate = 0m;
        try
        {
            todayRate = await rateProvider.GetEffectiveTodayRateAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            Core.Logging.AppLogger.LogWarn($"No se pudo leer la tasa efectiva del día al generar el comprobante de abono #{paymentId}: {ex.Message}", nameof(ReceiptsController));
        }

        var data = await _receiptService.GetCreditPaymentReceiptDataAsync(paymentId, todayRate, cancellationToken);
        if (data == null)
        {
            return this.ApiNotFound("El abono solicitado no existe.");
        }

        bool isElevated = User.IsInRole("Admin") || User.IsInRole("Manager");
        if (!isElevated)
        {
            if (string.IsNullOrEmpty(_currentUserService.UserId) || !int.TryParse(_currentUserService.UserId, out int uid) || data.ReceivedByUserId != uid)
            {
                return this.ApiForbidden("Acceso denegado: no tiene permisos para descargar el comprobante de este abono.");
            }
        }

        // Mismo contrato del recibo de venta: el comprobante honra el ajuste de formato de moneda.
        string? currencyFormat = null;
        try
        {
            currencyFormat = await _settingsService.GetSettingAsync("CurrencyFormat");
        }
        catch (Exception ex)
        {
            Core.Logging.AppLogger.LogWarn($"No se pudo leer CurrencyFormat al generar el comprobante de abono #{paymentId}: {ex.Message}", nameof(ReceiptsController));
        }

        var context = data.Context;
        var bytes = CreditPaymentReceiptPdfGenerator.BuildPdf(context, MoneyFormat.ParseFormat(currencyFormat));
        string fileName = $"Comprobante_Abono_{context.PaymentId}_{context.ReceivedAtUtc:yyyyMMdd_HHmmss}.pdf";
        return File(bytes, "application/pdf", fileName);
    }
}