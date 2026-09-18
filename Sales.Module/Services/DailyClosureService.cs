using Core.Helpers;
using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sales.Module.Data;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

public class DailyClosureService : IDailyClosureService
{
    private const int UnattributedChangeMethodId = 0;

    private readonly SalesDbContext _context;
    private readonly ITodayExchangeRateProvider _rateProvider;
    private readonly ICashDrawerService _cashDrawerService;

    public DailyClosureService(SalesDbContext context, ITodayExchangeRateProvider rateProvider, ICashDrawerService cashDrawerService)
    {
        _context = context;
        _rateProvider = rateProvider;
        _cashDrawerService = cashDrawerService;
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

        var allMethods = await _context.PaymentMethods
            .AsNoTracking()
            .Where(p => !p.IsDeleted)
            .OrderBy(p => p.DisplayOrder)
            .ThenBy(p => p.Name)
            .ToListAsync(cancellationToken);

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

        MergeMissingMethodsIntoClosure(closure, expectedTotals, existingMethodIds, methodEntities);
        RecalculateTotals(closure);

        _context.DailyClosures.Add(closure);
        await _context.SaveChangesAsync();

        return (await GetClosureAsync(closure.Id))!;
    }

    public async Task<DailyClosure?> GetClosureAsync(int id)
    {
        return await _context.DailyClosures
            .Include(dc => dc.Details)
            .FirstOrDefaultAsync(dc => dc.Id == id);
    }

    public async Task<DailyClosure?> GetLatestClosureAsync(CancellationToken cancellationToken = default)
    {
        return await _context.DailyClosures
            .AsNoTracking()
            .Include(dc => dc.Details)
            .OrderByDescending(dc => dc.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<string?> GetCashierDisplayNameAsync(int userId, CancellationToken cancellationToken = default)
    {
        var user = await _context.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        return user?.Name;
    }

    public async Task<CloseShiftResult> CreateClosureFromCommandAsync(
        CreateClosureCommand command,
        CancellationToken cancellationToken)
    {
        decimal exchangeRate = await _rateProvider.GetEffectiveTodayRateAsync(cancellationToken);
        if (exchangeRate <= 0)
        {
            throw new InvalidOperationException(
                "No se puede cerrar el turno: no existe una tasa BCV registrada para hoy. Registre la tasa del día antes de cerrar la caja.");
        }

        if (_context.Database.CurrentTransaction is not null)
        {
            return await ExecuteClosureCommandAsync(command, exchangeRate, cancellationToken);
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(() => ExecuteClosureCommandAsync(command, exchangeRate, cancellationToken));
    }

    private async Task<IDbContextTransaction?> OpenSerializableTransactionAsync(CancellationToken cancellationToken)
    {
        if (_context.Database.CurrentTransaction is not null)
        {
            return null;
        }

        if (_context.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true)
        {
            return null;
        }

        return await _context.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
    }

    private async Task<CloseShiftResult> ExecuteClosureCommandAsync(
        CreateClosureCommand command,
        decimal exchangeRate,
        CancellationToken cancellationToken)
    {
        IDbContextTransaction? transaction = await OpenSerializableTransactionAsync(cancellationToken);

        try
        {
            var expectedTotals = await GetExpectedTotalsByPaymentMethodAsync(command.ClosureDateUtc, cancellationToken);
            var expectedById = expectedTotals.ToDictionary(e => e.PaymentMethodId);

            ValidateDeclaredMethods(command.Declarations, expectedById);

            var details = new List<ClosureDetail>();
            var reportDetails = new List<ShiftReportDetailResult>();
            BuildDeclaredDetails(command.Declarations, expectedById, exchangeRate, details, reportDetails);

            var existingMethodIds = details.Select(d => d.PaymentMethodId).ToHashSet();
            var methodEntities = await _context.PaymentMethods
                .AsNoTracking()
                .Where(p => !p.IsDeleted)
                .ToDictionaryAsync(p => p.Id, cancellationToken);

            MergeMissingMethodsWithReport(details, reportDetails, expectedTotals, existingMethodIds, methodEntities, exchangeRate);

            var resolvedUser = await ResolveUserDetailsAsync(command.UserId, cancellationToken);

            var dailyClosure = new DailyClosure
            {
                ClosureDate = command.ClosureDateUtc,
                UserId = resolvedUser.UserId,
                Observation = resolvedUser.Observation,
                ExchangeRate = exchangeRate,
                Details = details
            };

            RecalculateTotals(dailyClosure);

            var savedClosure = await PersistClosureCoreAsync(dailyClosure);
            await _cashDrawerService.RolloverSessionAfterClosureAsync(exchangeRate);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            // 8.7-B5: los comprobantes se escriben DESPUÉS del commit, fuera de la transacción Serializable.
            await WriteClosedClosureReceiptsAsync(savedClosure, cancellationToken);

            return new CloseShiftResult(
                savedClosure.Id,
                resolvedUser.CashierName,
                resolvedUser.CashierCedula,
                savedClosure.ClosureDate,
                exchangeRate,
                reportDetails);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(cancellationToken);
            }
            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private static void BuildDeclaredDetails(
        IReadOnlyList<DeclaredPaymentAmount> declarations,
        Dictionary<int, ExpectedTotalDto> expectedById,
        decimal exchangeRate,
        List<ClosureDetail> details,
        List<ShiftReportDetailResult> reportDetails)
    {
        foreach (var declared in declarations)
        {
            var expected = expectedById[declared.PaymentMethodId];
            string currency = PaymentMethodCurrencyResolver.Resolve(expected.PaymentMethodName);

            decimal actualAmountBsS = currency == PaymentMethodCurrencyResolver.Usd
                ? declared.Amount * exchangeRate
                : declared.Amount;
            decimal expectedAmountBsS = expected.ExpectedAmountBsS;

            details.Add(new ClosureDetail
            {
                PaymentMethodId = declared.PaymentMethodId,
                PaymentMethodName = expected.PaymentMethodName,
                ExpectedAmountBsS = expectedAmountBsS,
                ActualAmountBsS = actualAmountBsS,
                DifferenceBsS = actualAmountBsS - expectedAmountBsS
            });

            decimal systemAmount = currency == PaymentMethodCurrencyResolver.Usd
                ? PricingCalculator.ToUSD(expectedAmountBsS, exchangeRate)
                : expectedAmountBsS;
            decimal declaredAmount = declared.Amount;
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
    }

    private async Task<(string UserId, string CashierName, string CashierCedula, string Observation)> ResolveUserDetailsAsync(
        string? inputUserId, CancellationToken cancellationToken)
    {
        string userId = inputUserId ?? "Cajero";
        string cashierName = "Cajero Activo";
        string cashierCedula = "V-00000000";
        string observation = "";

        if (int.TryParse(userId, out int userIdInt))
        {
            var user = await _context.Users.FindAsync(new object[] { userIdInt }, cancellationToken);
            if (user != null)
            {
                cashierName = user.Name;
                cashierCedula = user.Cedula ?? user.Username ?? "V-00000000";
                userId = user.Id.ToString();
                observation = cashierCedula;
            }
        }

        return (userId, cashierName, cashierCedula, observation);
    }

    private async Task<DailyClosure> PersistClosureCoreAsync(DailyClosure closure)
    {
        _context.DailyClosures.Add(closure);
        await _context.SaveChangesAsync();
        return (await GetClosureAsync(closure.Id))!;
    }

    private static void ValidateDeclaredMethods(
        IReadOnlyList<DeclaredPaymentAmount> declarations,
        Dictionary<int, ExpectedTotalDto> expectedById)
    {
        var unknownMethodIds = declarations
            .Where(d => !expectedById.ContainsKey(d.PaymentMethodId))
            .Select(d => d.PaymentMethodId)
            .Distinct()
            .ToList();

        if (unknownMethodIds.Count > 0)
        {
            throw new ArgumentException(
                $"El desglose contiene métodos de pago no reconocidos: {string.Join(", ", unknownMethodIds)}.",
                nameof(declarations));
        }
    }

    private static void MergeMissingMethodsIntoClosure(
        DailyClosure closure,
        List<ExpectedTotalDto> expectedTotals,
        HashSet<int> existingMethodIds,
        Dictionary<int, PaymentMethod> methodEntities)
    {
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

    private static void MergeMissingMethodsWithReport(
        List<ClosureDetail> details,
        List<ShiftReportDetailResult> reportDetails,
        List<ExpectedTotalDto> expectedTotals,
        HashSet<int> existingMethodIds,
        Dictionary<int, PaymentMethod> methodEntities,
        decimal exchangeRate)
    {
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
    }

    private static void RecalculateTotals(DailyClosure closure)
    {
        foreach (var detail in closure.Details)
        {
            if (detail.ActualAmountBsS < 0)
            {
                throw new ArgumentException(
                    $"El monto declarado para '{detail.PaymentMethodName}' no puede ser negativo.",
                    nameof(closure));
            }
            detail.DifferenceBsS = detail.ActualAmountBsS - detail.ExpectedAmountBsS;
        }

        closure.TotalExpectedBsS = closure.Details.Sum(d => d.ExpectedAmountBsS);
        closure.TotalActualBsS = closure.Details.Sum(d => d.ActualAmountBsS);
        closure.TotalDifferenceBsS = closure.TotalActualBsS - closure.TotalExpectedBsS;
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

    public async Task WriteClosedClosureReceiptsAsync(DailyClosure closure, CancellationToken cancellationToken = default)
    {
        if (closure == null) throw new ArgumentNullException(nameof(closure));

        try
        {
            bool isBlind = closure.UserId?.Contains("Cajero", StringComparison.OrdinalIgnoreCase) == true;
            string txtContent = GenerateReceiptContent(closure, isBlind);
            byte[] pdfBytes = ClosurePdfGenerator.GeneratePdf(closure, isBlind);

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
