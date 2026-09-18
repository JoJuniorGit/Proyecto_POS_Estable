using Core.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Sales.Module.Data;
using Sales.Module.DTOs;
using Sales.Module.Entities;
using Sales.Module.Interfaces;
using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

public partial class DailyClosureService : IDailyClosureService
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
            .ToDictionaryAsync(x => x.PaymentMethodId, x => x.TotalBsS, cancellationToken);

        var changeTotals = await _context.CashTransactions
            .AsNoTracking()
            .Where(ct => ct.Type == CashTransactionType.Expense
                && ct.Source == CashTransactionSource.SalePayment
                && ct.IsPhysicalCash
                && ct.TransactionTime >= startUtc
                && ct.TransactionTime < endUtc)
            .GroupBy(ct => ct.PaymentMethodId)
            .Select(g => new { PaymentMethodId = g.Key ?? UnattributedChangeMethodId, TotalBsS = g.Sum(ct => ct.AmountLocal) })
            .ToDictionaryAsync(x => x.PaymentMethodId, x => x.TotalBsS, cancellationToken);

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

    public async Task<DailyClosure> CreateClosureAsync(DailyClosure closure, CancellationToken cancellationToken = default)
    {
        if (_context.Database.CurrentTransaction is not null)
        {
            return await ExecuteClosureCoreAsync(closure, cancellationToken);
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(() => ExecuteClosureCoreAsync(closure, cancellationToken));
    }

    private async Task<DailyClosure> ExecuteClosureCoreAsync(DailyClosure closure, CancellationToken cancellationToken)
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

        var expectedTotals = await GetExpectedTotalsByPaymentMethodAsync(closure.ClosureDate, cancellationToken);
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
            .ToDictionaryAsync(p => p.Id, cancellationToken);

        MergeMissingMethodsIntoClosure(closure, expectedTotals, existingMethodIds, methodEntities);
        RecalculateTotals(closure);

        _context.DailyClosures.Add(closure);
        await _context.SaveChangesAsync(cancellationToken);

        return (await LoadClosureEntityAsync(closure.Id, cancellationToken))!;
    }

    public async Task<DailyClosureResponseDto?> GetClosureAsync(int id, CancellationToken cancellationToken = default)
    {
        var closure = await LoadClosureEntityAsync(id, cancellationToken);
        return closure is null ? null : ShiftReportMapper.MapClosure(closure);
    }

    private async Task<DailyClosure?> LoadClosureEntityAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _context.DailyClosures
            .AsNoTracking()
            .AsSplitQuery()
            .Include(dc => dc.Details)
            .FirstOrDefaultAsync(dc => dc.Id == id, cancellationToken);
    }

    public async Task<DailyClosureResponseDto?> GetLatestClosureAsync(CancellationToken cancellationToken = default)
    {
        var closure = await _context.DailyClosures
            .AsNoTracking()
            .AsSplitQuery()
            .Include(dc => dc.Details)
            .OrderByDescending(dc => dc.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return closure is null ? null : ShiftReportMapper.MapClosure(closure);
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

            var savedClosure = await PersistClosureCoreAsync(dailyClosure, cancellationToken);
            await _cashDrawerService.RolloverSessionAfterClosureAsync(exchangeRate, cancellationToken);

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

    private async Task<DailyClosureResponseDto> PersistClosureCoreAsync(DailyClosure closure, CancellationToken cancellationToken)
    {
        _context.DailyClosures.Add(closure);
        await _context.SaveChangesAsync(cancellationToken);
        return ShiftReportMapper.MapClosure((await LoadClosureEntityAsync(closure.Id, cancellationToken))!);
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
}
