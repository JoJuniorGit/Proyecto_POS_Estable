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
        var startOfDay = dateUtc.Date;
        var endOfDay = startOfDay.AddDays(1);

        // Fetch latest daily closure if any exists
        var lastClosure = await _context.DailyClosures
            .AsNoTracking()
            .OrderByDescending(dc => dc.ClosureDate)
            .FirstOrDefaultAsync();

        // Effective start time: if last closure occurred today (or after startOfDay), count sales after last closure
        var effectiveStartTime = (lastClosure != null && lastClosure.ClosureDate > startOfDay)
            ? lastClosure.ClosureDate
            : startOfDay;

        // Fetch all active payment methods ordered by priority
        var activeMethods = await _context.PaymentMethods
            .AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .ToListAsync();

        // Calculate expected sales totals per payment method for completed sales after effectiveStartTime
        var salesTotals = await _context.SalePayments
            .AsNoTracking()
            .Where(sp => sp.Sale != null
                && sp.Sale.Status == SaleStatus.Completed
                && sp.Sale.Date > effectiveStartTime
                && sp.Sale.Date < endOfDay)
            .GroupBy(sp => sp.PaymentMethodId)
            .Select(g => new { PaymentMethodId = g.Key, TotalBsS = g.Sum(sp => sp.AmountBsS) })
            .ToDictionaryAsync(x => x.PaymentMethodId, x => x.TotalBsS);

        var result = new List<ExpectedTotalDto>();
        foreach (var method in activeMethods)
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
        // Ensure all active payment methods are present in details
        var activeMethods = await _context.PaymentMethods
            .AsNoTracking()
            .Where(p => p.IsActive)
            .ToListAsync();

        var existingMethodIds = closure.Details.Select(d => d.PaymentMethodId).ToHashSet();
        foreach (var method in activeMethods)
        {
            if (!existingMethodIds.Contains(method.Id))
            {
                closure.Details.Add(new ClosureDetail
                {
                    PaymentMethodId = method.Id,
                    PaymentMethodName = method.Name,
                    ExpectedAmountBsS = 0m,
                    ActualAmountBsS = 0m,
                    DifferenceBsS = 0m
                });
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

        // Auto-save closure receipt copies to Downloads & Documents\Registro de cierres
        SaveClosureReceiptsSilently(savedClosure);

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

    private static void SaveClosureReceiptsSilently(DailyClosure closure)
    {
        try
        {
            bool isBlind = closure.UserId?.Contains("Cajero", StringComparison.OrdinalIgnoreCase) == true;
            string txtContent = GenerateReceiptContent(closure, isBlind);
            byte[] pdfBytes = ClosurePdfGenerator.GeneratePdf(closure, isBlind);

            string dateStamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            string pdfFileName = $"Cierre_{dateStamp}.pdf";
            string txtFileName = $"Cierre_{dateStamp}.txt";

            // Downloads folder path with fallback
            string downloadsDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (!System.IO.Directory.Exists(downloadsDir))
            {
                downloadsDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            }

            // CommonApplicationData\Registro de cierres folder path
            string commonAppDataDir = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
            string closureDir = System.IO.Path.Combine(commonAppDataDir, "Registro de cierres");

            System.IO.Directory.CreateDirectory(downloadsDir);
            System.IO.Directory.CreateDirectory(closureDir);

            // Write PDF copies
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(downloadsDir, pdfFileName), pdfBytes);
            System.IO.File.WriteAllBytes(System.IO.Path.Combine(closureDir, pdfFileName), pdfBytes);

            // Write TXT copies
            System.IO.File.WriteAllText(System.IO.Path.Combine(downloadsDir, txtFileName), txtContent);
            System.IO.File.WriteAllText(System.IO.Path.Combine(closureDir, txtFileName), txtContent);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[DailyClosureService] Warning: Failed to auto-save closure receipts: {ex.Message}");
        }
    }
}
