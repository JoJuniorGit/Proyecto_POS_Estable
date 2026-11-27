using Core.Common;
using Core.DTOs;
using Microsoft.EntityFrameworkCore;
using Sales.Module.DTOs.Profitability;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Sales.Module.Services;

/// <summary>
/// P0 (S3/S5): prorrateo compartido de los gastos operativos mensuales. Convierte la ventana UTC
/// del reporte a fechas locales con la zona del reporte, recorre los meses calendario solapados y
/// suma <c>MonthlyAmount * diasActivos / diasDelMes</c> por cada gasto activo y vigente. Es un
/// costo global: no se poda por filtros de producto ni de medio de pago.
/// </summary>
public partial class ProfitabilityService
{
    private async Task<decimal> LoadOperatingExpensesAsync(
        ProfitabilityTimeWindowResult window,
        ReportCurrencyContext reportCurrency,
        CancellationToken cancellationToken)
    {
        var timeZone = await ResolveWindowTimeZoneAsync(window.TimeZoneId, cancellationToken);
        var (fromDate, toExclusiveDate) = ResolveLocalDateRange(window, timeZone);
        if (toExclusiveDate <= fromDate)
        {
            return 0m;
        }

        var expenses = await LoadOperatingExpenseRowsAsync(reportCurrency, cancellationToken);
        var total = 0m;
        foreach (var expense in expenses)
        {
            if (expense.EffectiveFromDate >= toExclusiveDate)
            {
                continue;
            }

            if (expense.EffectiveToDate.HasValue && expense.EffectiveToDate.Value < fromDate)
            {
                continue;
            }

            total += ProrateByCalendarMonth(expense, expense.MonthlyAmount, fromDate, toExclusiveDate);
        }

        return total;
    }

    /// <summary>
    /// Loads the small recurring-expense configuration and resolves its monthly amount in the
    /// report currency. Shared by the headline and the series so both show the same global cost.
    /// </summary>
    private async Task<List<OperatingExpenseRow>> LoadOperatingExpenseRowsAsync(
        ReportCurrencyContext reportCurrency,
        CancellationToken cancellationToken)
    {
        var expenses = await _context.OperatingExpenses
            .AsNoTracking()
            .Where(expense => expense.IsActive)
            .Select(expense => new OperatingExpenseRow
            {
                MonthlyAmountUsd = expense.MonthlyAmountUsd,
                MonthlyAmountLocal = expense.MonthlyAmountLocal,
                LocalCurrencyCode = expense.LocalCurrencyCode,
                EffectiveFromDate = expense.EffectiveFromDate,
                EffectiveToDate = expense.EffectiveToDate
            })
            .ToListAsync(cancellationToken);

        foreach (var expense in expenses)
        {
            expense.MonthlyAmount = ResolveMonthlyAmount(expense, reportCurrency);
        }

        return expenses.Where(expense => expense.MonthlyAmount > 0m).ToList();
    }

    /// <summary>
    /// Resuelve el monto mensual en la moneda del reporte. En Dual manda el monto USD. En Single
    /// solo se usa el monto local cuando su moneda coincide con la moneda del reporte: no existe
    /// una tasa configurada para estos cargos y el sistema no la inventa. Si el monto local o su
    /// moneda faltan (o no coinciden), el gasto aporta 0 en ese reporte.
    /// </summary>
    private static decimal ResolveMonthlyAmount(
        OperatingExpenseRow expense,
        ReportCurrencyContext reportCurrency)
    {
        if (reportCurrency.Mode == MonetaryMode.Dual)
        {
            return expense.MonthlyAmountUsd;
        }

        if (expense.MonthlyAmountLocal is null || string.IsNullOrWhiteSpace(expense.LocalCurrencyCode))
        {
            return 0m;
        }

        return CurrencyMatches(expense.LocalCurrencyCode, reportCurrency.Code)
            ? expense.MonthlyAmountLocal.Value
            : 0m;
    }

    private static bool CurrencyMatches(string left, string right)
    {
        // Configuración persistida y perfil monetario pueden usar alias ("Bs.S" vs "VES"): se
        // comparan por el codigo ISO canonico y, si alguno no es reconocible, por texto exacto.
        var leftIso = CurrencyCodeNormalizer.NormalizeToIso(left);
        var rightIso = CurrencyCodeNormalizer.NormalizeToIso(right);
        if (leftIso is not null && rightIso is not null)
        {
            return string.Equals(leftIso, rightIso, StringComparison.Ordinal);
        }

        return string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static decimal ProrateByCalendarMonth(
        OperatingExpenseRow expense,
        decimal monthlyAmount,
        DateOnly windowFrom,
        DateOnly windowToExclusive)
    {
        var activeFrom = expense.EffectiveFromDate > windowFrom
            ? expense.EffectiveFromDate
            : windowFrom;

        var activeToExclusive = expense.EffectiveToDate.HasValue
            ? expense.EffectiveToDate.Value.AddDays(1)
            : DateOnly.MaxValue;
        if (activeToExclusive > windowToExclusive)
        {
            activeToExclusive = windowToExclusive;
        }

        if (activeToExclusive <= activeFrom)
        {
            return 0m;
        }

        var total = 0m;
        var monthStart = new DateOnly(activeFrom.Year, activeFrom.Month, 1);
        var lastActiveDate = activeToExclusive.AddDays(-1);
        var lastMonthStart = new DateOnly(lastActiveDate.Year, lastActiveDate.Month, 1);

        while (monthStart <= lastMonthStart)
        {
            var monthEndExclusive = monthStart.AddMonths(1);
            var daysInMonth = monthEndExclusive.DayNumber - monthStart.DayNumber;

            var segmentStart = monthStart > activeFrom ? monthStart : activeFrom;
            var segmentEndExclusive = monthEndExclusive < activeToExclusive
                ? monthEndExclusive
                : activeToExclusive;

            var activeDays = segmentEndExclusive.DayNumber - segmentStart.DayNumber;
            if (activeDays > 0)
            {
                total += monthlyAmount * activeDays / daysInMonth;
            }

            monthStart = monthEndExclusive;
        }

        return total;
    }

    private static (DateOnly From, DateOnly ToExclusive) ResolveLocalDateRange(
        ProfitabilityTimeWindowResult window,
        TimeZoneInfo timeZone)
    {
        var fromUtc = EnsureUtc(window.FromUtc);
        var toUtc = EnsureUtc(window.ToUtc);

        var fromLocal = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, timeZone);
        var toLocal = TimeZoneInfo.ConvertTimeFromUtc(toUtc, timeZone);

        // The report window is a half-open UTC interval. The exclusive local end is the local
        // midnight that closes it; a window that ends mid-day still counts the local day it
        // touches, because the proration unit is the calendar day.
        var toExclusiveDate = DateOnly.FromDateTime(toLocal);
        if (toLocal.TimeOfDay != TimeSpan.Zero)
        {
            toExclusiveDate = toExclusiveDate.AddDays(1);
        }

        return (DateOnly.FromDateTime(fromLocal), toExclusiveDate);
    }

    private async Task<TimeZoneInfo> ResolveWindowTimeZoneAsync(
        string timeZoneId,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(timeZoneId))
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
            }
            catch (TimeZoneNotFoundException)
            {
                // A custom zone (tests) or a zone removed from the host falls back to the same
                // provider that resolved the window; the date conversion stays consistent.
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        return await _timeZoneProvider.GetTimeZoneInfoAsync(cancellationToken);
    }

    private sealed class OperatingExpenseRow
    {
        public decimal MonthlyAmountUsd { get; init; }

        public decimal? MonthlyAmountLocal { get; init; }

        public string? LocalCurrencyCode { get; init; }

        public DateOnly EffectiveFromDate { get; init; }

        public DateOnly? EffectiveToDate { get; init; }

        /// <summary>Monthly amount resolved in the report currency by the loader.</summary>
        public decimal MonthlyAmount { get; set; }
    }
}
