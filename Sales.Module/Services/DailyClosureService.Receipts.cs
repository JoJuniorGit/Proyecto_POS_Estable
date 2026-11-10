using Core.Helpers;
using Sales.Module.DTOs;
using Sales.Module.Receipts;

namespace Sales.Module.Services;

public partial class DailyClosureService
{
    public static string GenerateReceiptContent(
        DailyClosureResponseDto closure,
        bool isBlind = false,
        MoneyDisplayFormat format = MoneyDisplayFormat.Venezuelan,
        string localCurrencyLabel = ReceiptCurrencyFormatter.LegacyLocalCode)
    {
        // 8.211-T4a (S10/F4a): rótulos con la moneda local del perfil (COP en Dual USD/COP);
        // el default conserva Bs.S para composiciones legacy sin perfil.
        string currencyLabel = ReceiptCurrencyFormatter.PresentationCode(localCurrencyLabel);
        if (string.IsNullOrEmpty(currencyLabel))
        {
            currencyLabel = ReceiptCurrencyFormatter.LegacyLocalCode;
        }

        var dateStr = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        var userName = string.IsNullOrWhiteSpace(closure.UserId) ? "Usuario" : closure.UserId;
        var sb = new System.Text.StringBuilder();

        if (isBlind)
        {
            sb.AppendLine("==========================================================================================");
            sb.AppendLine("                             COMPROBANTE DE ARQUEO A CIEGAS                               ");
            sb.AppendLine("==========================================================================================");
            sb.AppendLine($"Fecha/Hora: {dateStr}");
            sb.AppendLine($"Cajero:     {userName}");
            sb.AppendLine("------------------------------------------------------------------------------------------");
            sb.AppendLine(string.Format("{0,-25} {1,-8} {2,22}", "MÉTODO DE PAGO", "MONEDA", $"MONTO DECLARADO ({currencyLabel})"));
            sb.AppendLine("------------------------------------------------------------------------------------------");
            foreach (var detail in closure.Details)
            {
                // 8.211-T3b2 (S8/S10): misma clasificación que el mapper; el nombre es solo fallback.
                string curr = string.IsNullOrWhiteSpace(detail.PaymentMethodCurrency)
                    ? PaymentMethodCurrencyResolver.Resolve(detail.PaymentMethodName)
                    : detail.PaymentMethodCurrency;
                sb.AppendLine(string.Format(MoneyFormat.CultureFor(format), "{0,-25} {1,-8} {2,22:N2}", detail.PaymentMethodName, curr, detail.ActualAmountBsS));
            }
            sb.AppendLine("------------------------------------------------------------------------------------------");
            sb.AppendLine(string.Format(MoneyFormat.CultureFor(format), "{0,-25} {1,-8} {2,22:N2}", "TOTALES", "-", closure.TotalActualBsS));
            if (!string.IsNullOrWhiteSpace(closure.Observation))
            {
                sb.AppendLine($"Notas: {closure.Observation}");
            }
            sb.AppendLine("==========================================================================================");
        }
        else
        {
            string diffStatus = Math.Abs(closure.TotalDifferenceBsS) < 0.05m ? "Cuadrado" : (closure.TotalDifferenceBsS > 0 ? "Sobrante" : "Faltante");
            sb.AppendLine("==========================================================================================");
            sb.AppendLine("                       COMPROBANTE DE CIERRE Y AUDITORÍA DE CAJA                          ");
            sb.AppendLine("==========================================================================================");
            sb.AppendLine($"Fecha/Hora:    {dateStr}");
            sb.AppendLine($"Administrador: {userName}");
            sb.AppendLine("------------------------------------------------------------------------------------------");
            sb.AppendLine(string.Format("{0,-22} {1,-8} {2,22} {3,20} {4,18}", "MÉTODO DE PAGO", "MONEDA", $"MONTO DECLARADO ({currencyLabel})", $"MONTO SISTEMA ({currencyLabel})", $"DIFERENCIA ({currencyLabel})"));
            sb.AppendLine("------------------------------------------------------------------------------------------");
            foreach (var detail in closure.Details)
            {
                // 8.211-T3b2 (S8/S10): misma clasificación que el mapper; el nombre es solo fallback.
                string curr = string.IsNullOrWhiteSpace(detail.PaymentMethodCurrency)
                    ? PaymentMethodCurrencyResolver.Resolve(detail.PaymentMethodName)
                    : detail.PaymentMethodCurrency;
                sb.AppendLine(string.Format(MoneyFormat.CultureFor(format), "{0,-22} {1,-8} {2,22:N2} {3,20:N2} {4,18:N2}",
                    detail.PaymentMethodName,
                    curr,
                    detail.ActualAmountBsS,
                    detail.ExpectedAmountBsS,
                    detail.DifferenceBsS));
            }
            sb.AppendLine("------------------------------------------------------------------------------------------");
            sb.AppendLine(string.Format(MoneyFormat.CultureFor(format), "{0,-22} {1,-8} {2,22:N2} {3,20:N2} {4,18:N2}",
                "TOTALES",
                "-",
                closure.TotalActualBsS,
                closure.TotalExpectedBsS,
                closure.TotalDifferenceBsS));
            sb.AppendLine("------------------------------------------------------------------------------------------");
            sb.AppendLine($"TOTAL DECLARADO:  {currencyLabel} {MoneyFormat.Number(closure.TotalActualBsS, format),10}");
            sb.AppendLine($"TOTAL ESPERADO:   {currencyLabel} {MoneyFormat.Number(closure.TotalExpectedBsS, format),10}");
            sb.AppendLine($"DIFERENCIA TOTAL: {currencyLabel} {MoneyFormat.Number(closure.TotalDifferenceBsS, format),10}");
            sb.AppendLine($"ESTADO DE CAJA:   {diffStatus}");
            if (!string.IsNullOrWhiteSpace(closure.Observation))
            {
                sb.AppendLine($"Notas: {closure.Observation}");
            }
            sb.AppendLine("==========================================================================================");
        }

        return sb.ToString();
    }

    public async Task WriteClosedClosureReceiptsAsync(DailyClosureResponseDto closure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(closure);

        try
        {
            bool isBlind = closure.UserId?.Contains("Cajero", StringComparison.OrdinalIgnoreCase) == true;

            // 8.143: los comprobantes de cierre honran el ajuste CurrencyFormat; best-effort con default Venezuelan.
            var format = MoneyDisplayFormat.Venezuelan;
            if (_systemSettingsService != null)
            {
                try { format = MoneyFormat.ParseFormat(await _systemSettingsService.GetSettingAsync("CurrencyFormat")); }
                catch (Exception ex)
                {
                    Core.Logging.AppLogger.LogStart($"[DailyClosureReceipts] Error al leer CurrencyFormat ({ex.Message}); usando formato por defecto.");
                    format = MoneyDisplayFormat.Venezuelan;
                }
            }

            string localCurrencyLabel = await ResolveClosureCurrencyLabelAsync(cancellationToken);
            string txtContent = GenerateReceiptContent(closure, isBlind, format, localCurrencyLabel);
            byte[] pdfBytes = ClosurePdfGenerator.GeneratePdf(closure, isBlind, format, localCurrencyLabel);

            string dateStamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
            string uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
            string pdfFileName = $"Cierre_{dateStamp}_{closure.Id}_{uniqueSuffix}.pdf";
            string txtFileName = $"Cierre_{dateStamp}_{closure.Id}_{uniqueSuffix}.txt";

            string commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string primaryDir = System.IO.Path.Combine(commonAppData, "CommandCenterPOS", "Closures");
            string legacyCommonDir = System.IO.Path.Combine(commonAppData, "Registro de cierres");

            string docsDir = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            string userDocsDir = !string.IsNullOrWhiteSpace(docsDir) ? System.IO.Path.Combine(docsDir, "Registro de cierres") : string.Empty;

            var targetDirs = new System.Collections.Generic.List<string> { primaryDir, legacyCommonDir };
            if (!string.IsNullOrWhiteSpace(userDocsDir))
            {
                targetDirs.Add(userDocsDir);
            }

            foreach (var dir in targetDirs)
            {
                try
                {
                    if (!System.IO.Directory.Exists(dir))
                    {
                        System.IO.Directory.CreateDirectory(dir);
                    }

                    await TryWriteFileWithRetryAsync(System.IO.Path.Combine(dir, pdfFileName), pdfBytes, cancellationToken);
                    await TryWriteTextWithRetryAsync(System.IO.Path.Combine(dir, txtFileName), txtContent, cancellationToken);
                }
                catch (Exception ex)
                {
                    Core.Logging.AppLogger.LogWarn($"[DailyClosureService] Aviso al escribir comprobantes en '{dir}': {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Core.Logging.AppLogger.LogWarn($"[DailyClosureService] Advertencia general al auto-guardar comprobantes de cierre #{closure.Id}: {ex.Message}");
        }
    }

    /// <summary>
    /// 8.211-T4a (S10/F4a): moneda local presentada en los rótulos del cierre. Best-effort: el
    /// cierre ya está persistido y sus comprobantes no deben dejar de escribirse por un fallo de
    /// lectura de settings; sin perfil se conserva la presentación histórica Bs.S.
    /// </summary>
    private async Task<string> ResolveClosureCurrencyLabelAsync(CancellationToken cancellationToken)
    {
        if (_monetaryProfileService is null)
        {
            return ReceiptCurrencyFormatter.LegacyLocalCode;
        }

        try
        {
            var profile = await _monetaryProfileService.GetAsync(cancellationToken);
            string label = ReceiptCurrencyFormatter.PresentationCode(MonetaryRateResolver.ResolveLocalCurrency(profile));
            return string.IsNullOrEmpty(label) ? ReceiptCurrencyFormatter.LegacyLocalCode : label;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Core.Logging.AppLogger.LogWarn($"[DailyClosureReceipts] No se pudo leer el perfil monetario ({ex.Message}); se usa Bs.S en los rótulos del cierre.");
            return ReceiptCurrencyFormatter.LegacyLocalCode;
        }
    }

    private static async Task TryWriteFileWithRetryAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await System.IO.File.WriteAllBytesAsync(path, bytes, cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                Core.Logging.AppLogger.LogWarn($"[DailyClosureService] Intento {attempt + 1}/2 falló al escribir '{path}': {ex.Message}", "ReceiptWrite");
                if (attempt == 0) await Task.Delay(200, cancellationToken);
            }
        }
    }

    private static async Task TryWriteTextWithRetryAsync(string path, string text, CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                await System.IO.File.WriteAllTextAsync(path, text, cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                Core.Logging.AppLogger.LogWarn($"[DailyClosureService] Intento {attempt + 1}/2 falló al escribir '{path}': {ex.Message}", "ReceiptWrite");
                if (attempt == 0) await Task.Delay(200, cancellationToken);
            }
        }
    }
}
