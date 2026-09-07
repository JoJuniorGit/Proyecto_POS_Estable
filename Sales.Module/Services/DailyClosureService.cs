using Microsoft.EntityFrameworkCore;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Sales.Module.Services;

public class DailyClosureService : IDailyClosureService
{
    private readonly SalesDbContext _context;

    public DailyClosureService(SalesDbContext context)
    {
        _context = context;
    }

    public async Task<List<ExpectedTotalDto>> GetExpectedTotalsByPaymentMethodAsync(DateTime dateUtc)
    {
        // 1. Calcular ventana comercial de Venezuela en UTC (VET UTC-4, H-API-14)
        var venDate = Core.Helpers.TimeZoneHelper.GetVenezuelaDate(dateUtc);
        var tz = Core.Helpers.TimeZoneHelper.GetVenezuelaTimeZone();
        var startOfDayLocal = venDate.ToDateTime(TimeOnly.MinValue);
        var startOfDayUtc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(startOfDayLocal, DateTimeKind.Unspecified), tz);
        var endOfDayUtc = startOfDayUtc.AddDays(1);

        // Fetch latest daily closure if any exists
        var lastClosure = await _context.DailyClosures
            .AsNoTracking()
            .OrderByDescending(dc => dc.ClosureDate)
            .FirstOrDefaultAsync();

        // Effective start time: if last closure occurred after startOfDayUtc, count sales after last closure
        var effectiveStartTime = (lastClosure != null && lastClosure.ClosureDate > startOfDayUtc)
            ? lastClosure.ClosureDate
            : startOfDayUtc;

        // 1. Calculate expected sales totals per payment method for completed sales after effectiveStartTime
        var salesTotals = await _context.SalePayments
            .AsNoTracking()
            .Where(sp => sp.Sale != null
                && sp.Sale.Status == SaleStatus.Completed
                && sp.Sale.Date > effectiveStartTime
                && sp.Sale.Date < endOfDayUtc)
            .GroupBy(sp => sp.PaymentMethodId)
            .Select(g => new { PaymentMethodId = g.Key, TotalBsS = g.Sum(sp => sp.AmountBsS) })
            .ToDictionaryAsync(x => x.PaymentMethodId, x => x.TotalBsS);

        // 2. Fetch all payment methods not deleted ordered by priority
        var allMethods = await _context.PaymentMethods
            .AsNoTracking()
            .Where(p => !p.IsDeleted)
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .ToListAsync();

        // 3. Include active methods OR methods with historical sales in the period (even if deactivated)
        var relevantMethods = allMethods
            .Where(p => p.IsActive || salesTotals.ContainsKey(p.Id))
            .ToList();

        var result = new List<ExpectedTotalDto>();
        foreach (var method in relevantMethods)
        {
            salesTotals.TryGetValue(method.Id, out decimal expected);
            result.Add(new ExpectedTotalDto
            {
                PaymentMethodId = method.Id,
                PaymentMethodName = method.Name,
                ExpectedAmountBsS = expected
            });
        }

        return result;
    }

    public async Task<DailyClosure> CreateClosureAsync(DailyClosure closure)
    {
        // Ensure all relevant payment methods (active or with sales) are present in details
        var expectedTotals = await GetExpectedTotalsByPaymentMethodAsync(closure.ClosureDate);
        var expectedMap = expectedTotals.ToDictionary(e => e.PaymentMethodId, e => e.ExpectedAmountBsS);

        var existingMethodIds = closure.Details.Select(d => d.PaymentMethodId).ToHashSet();
        if (existingMethodIds.Count < expectedTotals.Count)
        {
            var methodEntities = await _context.PaymentMethods
                .AsNoTracking()
                .Where(p => !p.IsDeleted)
                .ToDictionaryAsync(p => p.Id);

            foreach (var exp in expectedTotals)
            {
                if (!existingMethodIds.Contains(exp.PaymentMethodId))
                {
                    methodEntities.TryGetValue(exp.PaymentMethodId, out var methodEntity);
                    decimal actualAmount = (methodEntity != null && methodEntity.IsCash) ? 0m : exp.ExpectedAmountBsS;

                    closure.Details.Add(new ClosureDetail
                    {
                        PaymentMethodId = exp.PaymentMethodId,
                        PaymentMethodName = exp.PaymentMethodName,
                        ExpectedAmountBsS = exp.ExpectedAmountBsS,
                        ActualAmountBsS = actualAmount,
                        DifferenceBsS = actualAmount - exp.ExpectedAmountBsS
                    });
                }
            }
        }

        // Recalculate differences to enforce domain rule
        foreach (var detail in closure.Details)
        {
            if (detail.ActualAmountBsS < 0)
            {
                throw new ArgumentException($"El monto declarado para '{detail.PaymentMethodName}' no puede ser negativo.", nameof(closure));
            }
            detail.DifferenceBsS = detail.ActualAmountBsS - detail.ExpectedAmountBsS;
        }

        closure.TotalExpectedBsS = closure.Details.Sum(d => d.ExpectedAmountBsS);
        closure.TotalActualBsS = closure.Details.Sum(d => d.ActualAmountBsS);
        closure.TotalDifferenceBsS = closure.TotalActualBsS - closure.TotalExpectedBsS;

        _context.DailyClosures.Add(closure);
        await _context.SaveChangesAsync();

        var savedClosure = (await GetClosureAsync(closure.Id))!;

        // 8.7-B5: la escritura de comprobantes se mueve FUERA de CreateClosureAsync; el caller
        // la invoca tras el commit de su transacción (WriteClosedClosureReceipts).

        return savedClosure;
    }

    public async Task<DailyClosure?> GetClosureAsync(int id)
    {
        return await _context.DailyClosures
            .Include(dc => dc.Details)
            .FirstOrDefaultAsync(dc => dc.Id == id);
    }

    public static string GenerateReceiptContent(DailyClosure closure, bool isBlind = false)
    {
        var dateStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
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
            sb.AppendLine(string.Format("{0,-25} {1,-8} {2,22}", "MÉTODO DE PAGO", "MONEDA", "MONTO DECLARADO (Bs.S)"));
            sb.AppendLine("------------------------------------------------------------------------------------------");
            foreach (var detail in closure.Details)
            {
                string curr = detail.PaymentMethodName.Contains("USD", StringComparison.OrdinalIgnoreCase) ? "USD" : "Bs.S";
                sb.AppendLine(string.Format("{0,-25} {1,-8} {2,22:N2}", detail.PaymentMethodName, curr, detail.ActualAmountBsS));
            }
            sb.AppendLine("------------------------------------------------------------------------------------------");
            sb.AppendLine(string.Format("{0,-25} {1,-8} {2,22:N2}", "TOTALES", "-", closure.TotalActualBsS));
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
            sb.AppendLine(string.Format("{0,-22} {1,-8} {2,22} {3,20} {4,18}", "MÉTODO DE PAGO", "MONEDA", "MONTO DECLARADO (Bs.S)", "MONTO SISTEMA (Bs.S)", "DIFERENCIA (Bs.S)"));
            sb.AppendLine("------------------------------------------------------------------------------------------");
            foreach (var detail in closure.Details)
            {
                string curr = detail.PaymentMethodName.Contains("USD", StringComparison.OrdinalIgnoreCase) ? "USD" : "Bs.S";
                sb.AppendLine(string.Format("{0,-22} {1,-8} {2,22:N2} {3,20:N2} {4,18:N2}", 
                    detail.PaymentMethodName, 
                    curr, 
                    detail.ActualAmountBsS, 
                    detail.ExpectedAmountBsS, 
                    detail.DifferenceBsS));
            }
            sb.AppendLine("------------------------------------------------------------------------------------------");
            sb.AppendLine(string.Format("{0,-22} {1,-8} {2,22:N2} {3,20:N2} {4,18:N2}", 
                "TOTALES", 
                "-", 
                closure.TotalActualBsS, 
                closure.TotalExpectedBsS, 
                closure.TotalDifferenceBsS));
            sb.AppendLine("------------------------------------------------------------------------------------------");
            sb.AppendLine($"TOTAL DECLARADO:  Bs.S {closure.TotalActualBsS,10:N2}");
            sb.AppendLine($"TOTAL ESPERADO:   Bs.S {closure.TotalExpectedBsS,10:N2}");
            sb.AppendLine($"DIFERENCIA TOTAL: Bs.S {closure.TotalDifferenceBsS,10:N2}");
            sb.AppendLine($"ESTADO DE CAJA:   {diffStatus}");
            if (!string.IsNullOrWhiteSpace(closure.Observation))
            {
                sb.AppendLine($"Notas: {closure.Observation}");
            }
            sb.AppendLine("==========================================================================================");
        }

        return sb.ToString();
    }

    public void WriteClosedClosureReceipts(DailyClosure closure)
    {
        if (closure == null) throw new ArgumentNullException(nameof(closure));

        // 8.7-B5: I/O de comprobantes CONFIRMADO, post-commit y fail-open (nunca rompe el cierre).
        try
        {
            bool isBlind = closure.UserId?.Contains("Cajero", StringComparison.OrdinalIgnoreCase) == true;
            string txtContent = GenerateReceiptContent(closure, isBlind);
            byte[] pdfBytes = ClosurePdfGenerator.GeneratePdf(closure, isBlind);

            string dateStamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
            string pdfFileName = $"Cierre_{dateStamp}_{closure.Id}_{uniqueSuffix}.pdf";
            string txtFileName = $"Cierre_{dateStamp}_{closure.Id}_{uniqueSuffix}.txt";

            // 1. Ruta segura y canónica del sistema para servicios: %ProgramData%\CommandCenterPOS\Closures
            string commonAppData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string primaryDir = System.IO.Path.Combine(commonAppData, "CommandCenterPOS", "Closures");
            string legacyCommonDir = System.IO.Path.Combine(commonAppData, "Registro de cierres");

            // 2. Ruta de documentos personales si está disponible en sesión interactiva
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

                    TryWriteFileWithRetry(System.IO.Path.Combine(dir, pdfFileName), pdfBytes);
                    TryWriteTextWithRetry(System.IO.Path.Combine(dir, txtFileName), txtContent);
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

    private static void TryWriteFileWithRetry(string path, byte[] bytes)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                System.IO.File.WriteAllBytes(path, bytes);
                return;
            }
            catch
            {
                if (attempt == 0) System.Threading.Thread.Sleep(200);
            }
        }
    }

    private static void TryWriteTextWithRetry(string path, string text)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                System.IO.File.WriteAllText(path, text);
                return;
            }
            catch
            {
                if (attempt == 0) System.Threading.Thread.Sleep(200);
            }
        }
    }
}
