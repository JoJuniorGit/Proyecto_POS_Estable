using Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

public class DailyClosureService : IDailyClosureService
{
    private const int UnattributedChangeMethodId = 0;

    private readonly SalesDbContext _context;

    public DailyClosureService(SalesDbContext context)
    {
        _context = context;
    }

    public async Task<List<ExpectedTotalDto>> GetExpectedTotalsByPaymentMethodAsync(DateTime dateUtc, CancellationToken cancellationToken = default)
    {
        var lastClosure = await _context.DailyClosures
            .AsNoTracking()
            .OrderByDescending(dc => dc.ClosureDate)
            .FirstOrDefaultAsync(cancellationToken);

        var activeSession = await _context.CashDrawerSessions
            .AsNoTracking()
            .Where(s => s.Status == CashDrawerStatus.Open)
            .OrderByDescending(s => s.OpenedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var (startUtc, endUtc) = ClosureWindowResolver.Resolve(
            dateUtc,
            activeSession?.OpenedAt,
            lastClosure?.ClosureDate);

        var salesTotals = await _context.SalePayments
            .AsNoTracking()
            .Where(sp => sp.Sale != null
                && sp.Sale.Status == SaleStatus.Completed
                && sp.Sale.Date >= startUtc
                && sp.Sale.Date < endUtc)
            .GroupBy(sp => sp.PaymentMethodId)
            .Select(g => new { PaymentMethodId = g.Key, TotalBsS = g.Sum(sp => sp.AmountBsS) })
            .ToDictionaryAsync(x => x.PaymentMethodId, x => x.TotalBsS);

        var changeTotals = await _context.CashTransactions
            .AsNoTracking()
            .Where(ct => ct.Type == CashTransactionType.Expense
                && ct.Source == CashTransactionSource.SalePayment
                && ct.IsPhysicalCash
                && ct.TransactionTime >= startUtc
                && ct.TransactionTime < endUtc)
            .GroupBy(ct => ct.PaymentMethodId)
            .Select(g => new { PaymentMethodId = g.Key ?? UnattributedChangeMethodId, TotalBsS = g.Sum(ct => ct.AmountLocal) })
            .ToDictionaryAsync(x => x.PaymentMethodId, x => x.TotalBsS);

        // 2. Fetch all payment methods not deleted ordered by priority
        var allMethods = await _context.PaymentMethods
            .AsNoTracking()
            .Where(p => !p.IsDeleted)
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .ToListAsync(cancellationToken);

        // 3. Include active methods OR methods with historical sales in the period (even if deactivated)
        var relevantMethods = allMethods
            .Where(p => p.IsActive || salesTotals.ContainsKey(p.Id))
            .ToList();

        var firstCashMethodId = relevantMethods.FirstOrDefault(p => p.IsCash)?.Id;

        var result = new List<ExpectedTotalDto>();
        foreach (var method in relevantMethods)
        {
            salesTotals.TryGetValue(method.Id, out decimal expected);

            if (method.IsCash)
            {
                changeTotals.TryGetValue(method.Id, out decimal change);
                if (method.Id == firstCashMethodId)
                {
                    changeTotals.TryGetValue(UnattributedChangeMethodId, out decimal unattributedChange);
                    change += unattributedChange;
                }
                expected -= change;
            }

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
        // 8.106-C1: resiliencia del servicio ante fallos transitorios. Los callers HTTP
        // (DailyClosureController/ShiftsController) ya envuelven TODO el bloque en strategy
        // externa + transacción Serializable (8.9-B4): si hay transacción activa, ejecutar el
        // cuerpo directo para que la strategy externa reintente el bloque completo; uso
        // standalone se auto-envuelve. Guard análogo a OutboxProcessorJob 8.9-B4.
        if (_context.Database.CurrentTransaction is not null)
        {
            return await ExecuteClosureCoreAsync(closure);
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(() => ExecuteClosureCoreAsync(closure));
    }

    private async Task<DailyClosure> ExecuteClosureCoreAsync(DailyClosure closure)
    {
        var duplicatedMethodIds = closure.Details
            .GroupBy(d => d.PaymentMethodId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicatedMethodIds.Count > 0)
        {
            throw new ArgumentException($"El desglose contiene métodos de pago duplicados: {string.Join(", ", duplicatedMethodIds)}.", nameof(closure));
        }

        // Ensure all relevant payment methods (active or with sales) are present in details
        var expectedTotals = await GetExpectedTotalsByPaymentMethodAsync(closure.ClosureDate);
        var expectedById = expectedTotals.ToDictionary(e => e.PaymentMethodId);

        var unknownMethodIds = closure.Details
            .Where(d => !expectedById.ContainsKey(d.PaymentMethodId))
            .Select(d => d.PaymentMethodId)
            .Distinct()
            .ToList();

        if (unknownMethodIds.Count > 0)
        {
            throw new ArgumentException($"El desglose contiene métodos de pago no reconocidos: {string.Join(", ", unknownMethodIds)}.", nameof(closure));
        }

        foreach (var detail in closure.Details)
        {
            detail.PaymentMethodName = expectedById[detail.PaymentMethodId].PaymentMethodName;
        }

        var existingMethodIds = closure.Details.Select(d => d.PaymentMethodId).ToHashSet();
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

        return (await GetClosureAsync(closure.Id))!;
        // 8.7-B5: la escritura de comprobantes se mueve FUERA de CreateClosureAsync; el caller
        // la invoca tras el commit de su transacción (WriteClosedClosureReceipts).
    }

    public async Task<DailyClosure?> GetClosureAsync(int id)
    {
        return await _context.DailyClosures
            .Include(dc => dc.Details)
            .FirstOrDefaultAsync(dc => dc.Id == id);
    }

    public async Task<CloseShiftResult> CreateClosureFromCommandAsync(
        CreateClosureCommand command,
        CancellationToken cancellationToken)
    {
        decimal exchangeRate = command.ExchangeRate;
        if (exchangeRate <= 0)
        {
            throw new InvalidOperationException(
                "No se puede cerrar el turno: no existe una tasa BCV registrada para hoy. Registre la tasa del día antes de cerrar la caja.");
        }

        var expectedTotals = await GetExpectedTotalsByPaymentMethodAsync(command.ClosureDateUtc, cancellationToken);
        var expectedById = expectedTotals.ToDictionary(e => e.PaymentMethodId);

        // Validate declared method ids
        var unknownMethodIds = command.Declarations
            .Where(d => !expectedById.ContainsKey(d.PaymentMethodId))
            .Select(d => d.PaymentMethodId)
            .Distinct()
            .ToList();

        if (unknownMethodIds.Count > 0)
        {
            throw new ArgumentException(
                $"El desglose contiene métodos de pago no reconocidos: {string.Join(", ", unknownMethodIds)}.",
                nameof(command));
        }

        var details = new List<ClosureDetail>();
        var reportDetails = new List<ShiftReportDetailResult>();

        foreach (var declared in command.Declarations)
        {
            var expected = expectedById[declared.PaymentMethodId];
            string currency = PaymentMethodCurrencyResolver.Resolve(expected.PaymentMethodName);

            decimal actualAmountBsS = currency == PaymentMethodCurrencyResolver.Usd
                ? declared.Amount * exchangeRate
                : declared.Amount;
            decimal expectedAmountBsS = expected.ExpectedAmountBsS;
            decimal diffBsS = actualAmountBsS - expectedAmountBsS;

            details.Add(new ClosureDetail
            {
                PaymentMethodId = declared.PaymentMethodId,
                PaymentMethodName = expected.PaymentMethodName,
                ExpectedAmountBsS = expectedAmountBsS,
                ActualAmountBsS = actualAmountBsS,
                DifferenceBsS = diffBsS
            });

            decimal systemAmount = currency == PaymentMethodCurrencyResolver.Usd
                ? PricingCalculator.ToUSD(expectedAmountBsS, exchangeRate)
                : expectedAmountBsS;
            decimal declaredAmount = currency == PaymentMethodCurrencyResolver.Usd
                ? declared.Amount
                : declared.Amount;
            decimal diff = declaredAmount - systemAmount;
            string status = Math.Abs(diff) < 0.05m ? "Balanced" : (diff > 0 ? "Surplus" : "Shortage");

            reportDetails.Add(new ShiftReportDetailResult(
                declared.PaymentMethodId,
                expected.PaymentMethodName,
                currency,
                declaredAmount,
                systemAmount,
                diff,
                status));
        }

        var existingMethodIds = details.Select(d => d.PaymentMethodId).ToHashSet();
        var methodEntities = await _context.PaymentMethods
            .AsNoTracking()
            .Where(p => !p.IsDeleted)
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        foreach (var exp in expectedTotals)
        {
            if (!existingMethodIds.Contains(exp.PaymentMethodId))
            {
                methodEntities.TryGetValue(exp.PaymentMethodId, out var methodEntity);
                decimal actualAmount = (methodEntity != null && methodEntity.IsCash) ? 0m : exp.ExpectedAmountBsS;

                details.Add(new ClosureDetail
                {
                    PaymentMethodId = exp.PaymentMethodId,
                    PaymentMethodName = exp.PaymentMethodName,
                    ExpectedAmountBsS = exp.ExpectedAmountBsS,
                    ActualAmountBsS = actualAmount,
                    DifferenceBsS = actualAmount - exp.ExpectedAmountBsS
                });

                string expCurrency = PaymentMethodCurrencyResolver.Resolve(exp.PaymentMethodName);
                reportDetails.Add(new ShiftReportDetailResult(
                    exp.PaymentMethodId,
                    exp.PaymentMethodName,
                    expCurrency,
                    actualAmount,
                    expCurrency == PaymentMethodCurrencyResolver.Usd
                        ? PricingCalculator.ToUSD(exp.ExpectedAmountBsS, exchangeRate)
                        : exp.ExpectedAmountBsS,
                    actualAmount - (expCurrency == PaymentMethodCurrencyResolver.Usd
                        ? PricingCalculator.ToUSD(exp.ExpectedAmountBsS, exchangeRate)
                        : exp.ExpectedAmountBsS),
                    "Balanced"));
            }
        }

        var dailyClosure = new DailyClosure
        {
            ClosureDate = command.ClosureDateUtc,
            UserId = command.UserId ?? "Cajero",
            Observation = command.Observation,
            ExchangeRate = exchangeRate,
            Details = details
        };

        DailyClosure savedClosure;
        if (_context.Database.CurrentTransaction is not null)
        {
            savedClosure = await ExecuteClosureCoreAsync(dailyClosure);
        }
        else
        {
            var strategy = _context.Database.CreateExecutionStrategy();
            savedClosure = await strategy.ExecuteAsync(() => ExecuteClosureCoreAsync(dailyClosure));
        }

        return new CloseShiftResult(
            savedClosure.Id,
            command.UserId ?? "Cajero",
            command.Observation ?? "V-00000000",
            savedClosure.ClosureDate,
            exchangeRate,
            reportDetails);
    }

    public static string GenerateReceiptContent(DailyClosure closure, bool isBlind = false)
    {
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
            sb.AppendLine(string.Format("{0,-25} {1,-8} {2,22}", "MÉTODO DE PAGO", "MONEDA", "MONTO DECLARADO (Bs.S)"));
            sb.AppendLine("------------------------------------------------------------------------------------------");
            foreach (var detail in closure.Details)
            {
                string curr = PaymentMethodCurrencyResolver.Resolve(detail.PaymentMethodName);
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
                string curr = PaymentMethodCurrencyResolver.Resolve(detail.PaymentMethodName);
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

            string dateStamp = DateTime.UtcNow.ToString("yyyy-MM-dd_HH-mm-ss");
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
